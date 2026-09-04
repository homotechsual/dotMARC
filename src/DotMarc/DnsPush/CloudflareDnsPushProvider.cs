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
    private readonly ILogger<CloudflareDnsPushProvider> _logger;

    public CloudflareDnsPushProvider(IDbContextFactory<DotMarcDbContext> dbFactory, ISecretStore secretStore, HttpClient http, ILogger<CloudflareDnsPushProvider> logger)
    {
        _dbFactory = dbFactory;
        _secretStore = secretStore;
        _http = http;
        _logger = logger;
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
            // dns.write alone can create/update records once their ID is known, but two lookups
            // need separate read scopes first: resolving a domain name to its zone ID
            // (FindZoneIdAsync's GET /zones?name=...) needs zone.read, and finding an existing
            // record's ID before updating it (UpdateExistingRecordAsync's GET .../dns_records)
            // needs dns.read — confirmed live: zone.read alone fixed the zone lookup but the
            // record-merge path still failed the same way, on the existing-record lookup.
            ["scope"] = "zone.read dns.read dns.write",
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
        var rawBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        // TEMPORARY diagnostic logging while chasing an unexplained "zone not found" outcome —
        // remove once resolved. The raw body carries zone metadata (names, IDs, plan info) but
        // nothing secret — the bearer token itself is never logged.
        _logger.LogWarning("Cloudflare zone lookup for {ZoneName}: status={StatusCode}, body={Body}", zoneName, (int)response.StatusCode, rawBody);
        if (!response.IsSuccessStatusCode)
        {
            return (null, (int)response.StatusCode);
        }
        var zones = System.Text.Json.JsonSerializer.Deserialize<ApiResponse<List<IdRecord>>>(rawBody);
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
        // TEMPORARY diagnostic logging while chasing an unexplained record-push failure — remove
        // once resolved.
        var rawBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("Cloudflare record create for {RecordType} {RecordName}: status={StatusCode}, body={Body}",
            change.RecordType, change.Name, (int)response.StatusCode, rawBody);
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
        var findRawBody = await findResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        // TEMPORARY diagnostic logging while chasing an unexplained "zone not found" outcome on
        // the record-merge path specifically — remove once resolved.
        _logger.LogWarning("Cloudflare existing-record lookup for {RecordType} {RecordName}: status={StatusCode}, body={Body}",
            change.RecordType, change.Name, (int)findResponse.StatusCode, findRawBody);
        if (!findResponse.IsSuccessStatusCode)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Cloudflare rejected the record lookup ({(int)findResponse.StatusCode}).");
        }
        var existing = System.Text.Json.JsonSerializer.Deserialize<ApiResponse<List<IdRecord>>>(findRawBody);
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
        // TEMPORARY diagnostic logging while chasing an unexplained record-push failure — remove
        // once resolved.
        var updateRawBody = await updateResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("Cloudflare record update for {RecordType} {RecordName}: status={StatusCode}, body={Body}",
            change.RecordType, change.Name, (int)updateResponse.StatusCode, updateRawBody);
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
