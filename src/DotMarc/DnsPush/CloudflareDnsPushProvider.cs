using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.DnsPush;

/// <summary>Pushes a DNS record change to Cloudflare, authenticated via a fresh OAuth 2.0
/// Authorization Code + PKCE exchange each time — see the design spec's "Auth model" section for
/// why nothing about the end-user's push-time token is ever persisted. The app's own OAuth client
/// credentials (registered once per deployment with Cloudflare) are DB-backed
/// (CloudflareDnsSettings/ISecretStore), read fresh per call since they're admin-editable at
/// runtime. Endpoints confirmed against Cloudflare's own OIDC discovery document
/// (https://dash.cloudflare.com/.well-known/openid-configuration); the DNS API itself is
/// documented at https://developers.cloudflare.com/api/resources/dns/subresources/records/.</summary>
public sealed class CloudflareDnsPushProvider : IDnsPushProvider
{
    private const string AuthorizationEndpoint = "https://dash.cloudflare.com/oauth2/auth";
    private const string TokenEndpoint = "https://dash.cloudflare.com/oauth2/token";
    private const string ApiBase = "https://api.cloudflare.com/client/v4";

    private readonly IDbContextFactory<DotMarcDbContext> _dbFactory;
    private readonly ISecretStore _secretStore;
    private readonly HttpClient _http;

    public CloudflareDnsPushProvider(IDbContextFactory<DotMarcDbContext> dbFactory, ISecretStore secretStore, HttpClient http)
    {
        _dbFactory = dbFactory;
        _secretStore = secretStore;
        _http = http;
    }

    public string ProviderKey => "cloudflare";

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrEmpty(settings.ClientId) && settings.ClientSecretConfigured;
    }

    public async Task<string> BuildAuthorizationUrlAsync(string state, string codeChallenge, string redirectUri, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = settings.ClientId!,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "com.cloudflare.api.account.zone.dns",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };
        return AuthorizationEndpoint + "?" + string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    public async Task<DnsPushResult> ExchangeAndPushAsync(
        string code, string codeVerifier, string redirectUri, IReadOnlyList<DnsRecordChange> changes, CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var clientSecret = await _secretStore.GetSecretAsync(CloudflareDnsSettings.SecretStoreKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(settings.ClientId) || string.IsNullOrEmpty(clientSecret))
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, "Cloudflare push is not configured for this deployment.");
        }

        var accessToken = await ExchangeCodeForTokenAsync(settings.ClientId, clientSecret, code, codeVerifier, redirectUri, cancellationToken).ConfigureAwait(false);
        if (accessToken is null)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, "Cloudflare rejected the authorization code exchange.");
        }

        foreach (var change in changes)
        {
            var result = await PushOneChangeAsync(change, accessToken, cancellationToken).ConfigureAwait(false);
            if (result.Outcome != DnsPushOutcome.Pushed)
            {
                return result;
            }
        }

        return new DnsPushResult(DnsPushOutcome.Pushed, null);
    }

    private async Task<DnsPushResult> PushOneChangeAsync(DnsRecordChange change, string accessToken, CancellationToken cancellationToken)
    {
        var zoneName = change.ZoneName;
        var (zoneId, zoneErrorStatus) = await FindZoneIdAsync(zoneName, accessToken, cancellationToken).ConfigureAwait(false);
        if (zoneErrorStatus.HasValue)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Cloudflare rejected the zone lookup ({zoneErrorStatus}).");
        }
        if (zoneId is null)
        {
            return new DnsPushResult(DnsPushOutcome.ZoneNotFound, $"Couldn't find {zoneName} in the Cloudflare account you authorized.");
        }

        return change.Kind == DnsRecordChangeKind.Merge
            ? await UpdateExistingRecordAsync(zoneId, accessToken, change, cancellationToken).ConfigureAwait(false)
            : await CreateRecordAsync(zoneId, accessToken, change, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CloudflareDnsSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await CloudflareDnsSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ExchangeCodeForTokenAsync(string clientId, string clientSecret, string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code_verifier"] = codeVerifier
            })
        };
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return token?.AccessToken;
    }

    private async Task<(string? Id, int? ErrorStatusCode)> FindZoneIdAsync(string zoneName, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/zones?name={Uri.EscapeDataString(zoneName)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (null, (int)response.StatusCode);
        }
        var zones = await response.Content.ReadFromJsonAsync<ApiResponse<List<IdRecord>>>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return (zones?.Result?.FirstOrDefault()?.Id, null);
    }

    private async Task<DnsPushResult> CreateRecordAsync(string zoneId, string accessToken, DnsRecordChange change, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/zones/{zoneId}/dns_records")
        {
            Content = JsonContent.Create(new DnsRecordPayload(change.RecordType, change.Name, change.DesiredValue))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? new DnsPushResult(DnsPushOutcome.Pushed, null)
            : new DnsPushResult(DnsPushOutcome.ProviderError, $"Cloudflare rejected the record push ({(int)response.StatusCode}).");
    }

    private async Task<DnsPushResult> UpdateExistingRecordAsync(string zoneId, string accessToken, DnsRecordChange change, CancellationToken cancellationToken)
    {
        using var findRequest = new HttpRequestMessage(HttpMethod.Get,
            $"{ApiBase}/zones/{zoneId}/dns_records?type={change.RecordType}&name={Uri.EscapeDataString(change.Name)}");
        findRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var findResponse = await _http.SendAsync(findRequest, cancellationToken).ConfigureAwait(false);
        if (!findResponse.IsSuccessStatusCode)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Cloudflare rejected the record lookup ({(int)findResponse.StatusCode}).");
        }
        var existing = await findResponse.Content.ReadFromJsonAsync<ApiResponse<List<IdRecord>>>(cancellationToken: cancellationToken).ConfigureAwait(false);
        var recordId = existing?.Result?.FirstOrDefault()?.Id;
        if (recordId is null)
        {
            return new DnsPushResult(DnsPushOutcome.ZoneNotFound, $"{change.Name} no longer exists at Cloudflare — it may have been removed since this page loaded.");
        }

        using var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"{ApiBase}/zones/{zoneId}/dns_records/{recordId}")
        {
            Content = JsonContent.Create(new DnsRecordPayload(change.RecordType, change.Name, change.DesiredValue))
        };
        updateRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var updateResponse = await _http.SendAsync(updateRequest, cancellationToken).ConfigureAwait(false);
        return updateResponse.IsSuccessStatusCode
            ? new DnsPushResult(DnsPushOutcome.Pushed, null)
            : new DnsPushResult(DnsPushOutcome.ProviderError, $"Cloudflare rejected the record update ({(int)updateResponse.StatusCode}).");
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);
    private sealed record ApiResponse<T>([property: JsonPropertyName("result")] T? Result);
    private sealed record IdRecord([property: JsonPropertyName("id")] string Id);
    private sealed record DnsRecordPayload(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("content")] string Content);
}
