// src/DotMarc/Notifications/HaloPsaTokenCache.cs
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DotMarc.Notifications;

/// <summary>Caches the OAuth2 client_credentials token in memory for the lifetime of this
/// singleton instance — safe even across multiple Container Apps replicas, since each replica
/// just acquires its own token independently; no shared/distributed cache is needed at this call
/// volume (alert-triggered, not a per-request hot path).</summary>
public sealed class HaloPsaTokenCache
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAtUtc;

    public async Task<string> GetTokenAsync(HttpClient httpClient, HaloPsaSettings settings, string clientSecret, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresAtUtc)
            {
                return _token;
            }

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

            _token = payload!.AccessToken;
            // Refresh a minute early so a call starting right before expiry doesn't race a 401.
            _expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresInSeconds - 60);
            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresInSeconds);
}
