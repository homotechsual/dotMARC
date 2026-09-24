// src/DotMarc/Notifications/HaloPsaTokenCache.cs
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.Notifications;

/// <summary>Caches the OAuth2 client_credentials token in memory for the lifetime of this
/// singleton instance - safe even across multiple Container Apps replicas, since each replica
/// just acquires its own token independently; no shared/distributed cache is needed at this call
/// volume (alert-triggered, not a per-request hot path). Keyed on (AuthServerUrl, ClientId) so
/// that changing the configured Halo credentials from Alert settings naturally misses the old
/// cache entry rather than reusing a token minted from stale credentials for up to an hour.</summary>
public sealed class HaloPsaTokenCache
{
    /// <summary>Halo rejects the whole request with invalid_scope if any one scope is unknown, and it has
    /// no scopes for agents or teams (those follow the API agent's role), so this stays the minimum that works.</summary>
    private const string Scope = "edit:tickets read:tickets read:customers";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<(string AuthServerUrl, string ClientId), (string Token, DateTimeOffset ExpiresAtUtc, string? GrantedScope)> _tokensByKey = new();

    public async Task<string> GetTokenAsync(HttpClient httpClient, HaloPsaSettings settings, string clientSecret, CancellationToken cancellationToken)
    {
        var key = KeyFor(settings);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tokensByKey.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow < cached.ExpiresAtUtc)
            {
                return cached.Token;
            }

            var (token, expiresAtUtc, grantedScope) = await AcquireTokenAsync(httpClient, settings, clientSecret, cancellationToken).ConfigureAwait(false);
            _tokensByKey[key] = (token, expiresAtUtc, grantedScope);
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The scope Halo said it granted the cached token, or null when there is no token yet or
    /// Halo didn't report one. A 403 on a call usually means the token lacks the scope the endpoint needs,
    /// and this is the only place that shows what Halo actually granted.</summary>
    public string? GrantedScopeFor(HaloPsaSettings settings) =>
        _tokensByKey.TryGetValue(KeyFor(settings), out var cached) ? cached.GrantedScope : null;

    /// <summary>Drops the cached token for this settings' credentials, e.g. after a 401 - the next
    /// <see cref="GetTokenAsync"/> call for the same key acquires a fresh one instead of reusing a
    /// token Halo has already rejected.</summary>
    public async Task InvalidateAsync(HaloPsaSettings settings, CancellationToken cancellationToken)
    {
        var key = KeyFor(settings);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _tokensByKey.Remove(key);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static (string, string) KeyFor(HaloPsaSettings settings) => (settings.AuthServerUrl!, settings.ClientId!);

    private static async Task<(string Token, DateTimeOffset ExpiresAtUtc, string? GrantedScope)> AcquireTokenAsync(HttpClient httpClient, HaloPsaSettings settings, string clientSecret, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.AuthServerUrl!.TrimEnd('/')}/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = settings.ClientId!,
                ["client_secret"] = clientSecret,
                ["scope"] = Scope
            })
        };

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(await DescribeRejectionAsync(response, cancellationToken).ConfigureAwait(false), null, response.StatusCode);
        }

        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);

        // Refresh a minute early so a call starting right before expiry doesn't race a 401.
        return (payload!.AccessToken, DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresInSeconds - 60), payload.Scope);
    }

    /// <summary>Halo's token endpoint explains a rejection in an OAuth error body (invalid_scope,
    /// invalid_client, and so on). Surfacing it turns an opaque "400" into something an admin can act
    /// on. The body describes the error only and never echoes the client secret back.</summary>
    private static async Task<string> DescribeRejectionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var summary = $"HaloPSA rejected the token request ({(int)response.StatusCode} {response.ReasonPhrase})";
        try
        {
            var rejection = await response.Content.ReadFromJsonAsync<TokenRejection>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(rejection?.Error))
            {
                return string.IsNullOrWhiteSpace(rejection.Description)
                    ? $"{summary}: {rejection.Error}"
                    : $"{summary}: {rejection.Error} - {rejection.Description}";
            }
        }
        catch (JsonException)
        {
            // Not an OAuth error body, so the status code alone will have to do.
        }

        return summary;
    }

    private sealed record TokenRejection(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? Description);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresInSeconds,
        [property: JsonPropertyName("scope")] string? Scope = null);
}
