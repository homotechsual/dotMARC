// src/DotMarc/Notifications/HaloPsaTokenCache.cs
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DotMarc.Notifications;

/// <summary>Caches the OAuth2 client_credentials token in memory for the lifetime of this
/// singleton instance — safe even across multiple Container Apps replicas, since each replica
/// just acquires its own token independently; no shared/distributed cache is needed at this call
/// volume (alert-triggered, not a per-request hot path). Keyed on (AuthServerUrl, ClientId) so
/// that changing the configured Halo credentials from Alert settings naturally misses the old
/// cache entry rather than reusing a token minted from stale credentials for up to an hour.</summary>
public sealed class HaloPsaTokenCache
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<(string AuthServerUrl, string ClientId), (string Token, DateTimeOffset ExpiresAtUtc)> _tokensByKey = new();

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

            var (token, expiresAtUtc) = await AcquireTokenAsync(httpClient, settings, clientSecret, cancellationToken).ConfigureAwait(false);
            _tokensByKey[key] = (token, expiresAtUtc);
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Drops the cached token for this settings' credentials, e.g. after a 401 — the next
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

    private static async Task<(string Token, DateTimeOffset ExpiresAtUtc)> AcquireTokenAsync(HttpClient httpClient, HaloPsaSettings settings, string clientSecret, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.AuthServerUrl!.TrimEnd('/')}/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = settings.ClientId!,
                ["client_secret"] = clientSecret,
                ["scope"] = "edit:tickets read:tickets read:customers read:teams"
            })
        };

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);

        // Refresh a minute early so a call starting right before expiry doesn't race a 401.
        return (payload!.AccessToken, DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresInSeconds - 60));
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresInSeconds);
}
