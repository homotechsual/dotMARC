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

        return change.Kind switch
        {
            DnsRecordChangeKind.Merge => await UpdateExistingRecordAsync(zoneId, accessToken, change, cancellationToken).ConfigureAwait(false),
            DnsRecordChangeKind.Replace => await ReplaceRecordAsync(zoneId, accessToken, change, cancellationToken).ConfigureAwait(false),
            _ => await CreateRecordAsync(zoneId, accessToken, change, cancellationToken).ConfigureAwait(false)
        };
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
            Content = JsonContent.Create(new DnsRecordPayload(change.RecordType, change.Name, BuildContent(change)))
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
            Content = JsonContent.Create(new DnsRecordPayload(change.RecordType, change.Name, BuildContent(change)))
        };
        updateRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var updateResponse = await _http.SendAsync(updateRequest, cancellationToken).ConfigureAwait(false);
        return updateResponse.IsSuccessStatusCode
            ? new DnsPushResult(DnsPushOutcome.Pushed, null)
            : new DnsPushResult(DnsPushOutcome.ProviderError, $"Cloudflare rejected the record update ({(int)updateResponse.StatusCode}).");
    }

    /// <summary>Deletes whatever record of change.ExistingRecordType currently exists at
    /// change.Name, then creates a change.RecordType record with change.DesiredValue in its place.
    /// DNS doesn't allow a CNAME to coexist with any other record type at the same name, so this is
    /// the only way to convert a third-party CNAME delegation into a record dotMARC manages
    /// directly — there is no in-place "change the record type" operation. If the delete succeeds
    /// but the create then fails, the name is left with neither record; that failure is reported
    /// explicitly (by design — see the design spec's failure-handling decision) rather than
    /// attempting an automatic rollback.</summary>
    private async Task<DnsPushResult> ReplaceRecordAsync(string zoneId, string accessToken, DnsRecordChange change, CancellationToken cancellationToken)
    {
        var existingType = change.ExistingRecordType ?? change.RecordType;
        string? recordId;
        try
        {
            using var findRequest = new HttpRequestMessage(HttpMethod.Get,
                $"{ApiBase}/zones/{zoneId}/dns_records?type={Uri.EscapeDataString(existingType)}&name={Uri.EscapeDataString(change.Name)}");
            findRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var findResponse = await _http.SendAsync(findRequest, cancellationToken).ConfigureAwait(false);
            if (!findResponse.IsSuccessStatusCode)
            {
                return new DnsPushResult(DnsPushOutcome.ProviderError, $"Cloudflare rejected the record lookup ({(int)findResponse.StatusCode}) — nothing was changed.");
            }
            var existing = await findResponse.Content.ReadFromJsonAsync<ApiResponse<List<IdRecord>>>(cancellationToken: cancellationToken).ConfigureAwait(false);
            recordId = existing?.Result?.FirstOrDefault()?.Id;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Nothing has been changed yet at this point, so this is a plain, safe-to-retry
            // failure, not the "domain now has no record" case ReplaceFailedAfterDelete exists for.
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Couldn't reach Cloudflare to look up the existing {existingType} record: {ex.Message} — nothing was changed.");
        }

        if (recordId is null)
        {
            // Deliberately ProviderError, not ZoneNotFound — the zone WAS found; this specific
            // record just isn't there anymore (most likely removed since this page loaded, a
            // benign race a retry would clear). Reusing ZoneNotFound's "check the account you
            // authorized" message here would send the admin chasing a permissions problem that
            // doesn't exist.
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"The {existingType} record at {change.Name} no longer exists at Cloudflare — it may have been removed since this page loaded. Nothing was changed; try again.");
        }

        try
        {
            using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"{ApiBase}/zones/{zoneId}/dns_records/{recordId}");
            deleteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var deleteResponse = await _http.SendAsync(deleteRequest, cancellationToken).ConfigureAwait(false);
            if (!deleteResponse.IsSuccessStatusCode)
            {
                return new DnsPushResult(DnsPushOutcome.ProviderError, $"Cloudflare rejected deleting the existing {existingType} record ({(int)deleteResponse.StatusCode}) — nothing was changed.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Couldn't reach Cloudflare to delete the existing {existingType} record: {ex.Message} — nothing was changed.");
        }

        // The delete above succeeded — from here on, any failure means the name now has NO
        // record at all, which is why every remaining failure path returns
        // ReplaceFailedAfterDelete instead of the generic ProviderError.
        try
        {
            using var createRequest = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/zones/{zoneId}/dns_records")
            {
                Content = JsonContent.Create(new DnsRecordPayload(change.RecordType, change.Name, BuildContent(change)))
            };
            createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var createResponse = await _http.SendAsync(createRequest, cancellationToken).ConfigureAwait(false);
            return createResponse.IsSuccessStatusCode
                ? new DnsPushResult(DnsPushOutcome.Pushed, null)
                : new DnsPushResult(DnsPushOutcome.ReplaceFailedAfterDelete, $"The old {existingType} record at {change.Name} was deleted, but creating the new {change.RecordType} record failed ({(int)createResponse.StatusCode}) — this name now has no record and needs manual attention.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new DnsPushResult(DnsPushOutcome.ReplaceFailedAfterDelete, $"The old {existingType} record at {change.Name} was deleted, but Cloudflare couldn't be reached to create the new {change.RecordType} record: {ex.Message} — this name now has no record and needs manual attention.");
        }
    }

    /// <summary>Cloudflare's own API treats an unquoted TXT content value as non-conformant — it
    /// accepts it and normalizes on their side (functionally identical either way), but flags the
    /// record with a validation warning in their dashboard. Wrapping it here avoids that warning and
    /// matches Cloudflare's documented format. Every value this app pushes (DMARC/TLSRPT policy
    /// text, the MTA-STS asuid verification token) is plain text with no embedded quotes, so the
    /// escape only guards against a value that happens to contain one. CNAME (and any other
    /// non-TXT type) content is never zone-file text and must NOT be quoted.</summary>
    private static string BuildContent(DnsRecordChange change) =>
        string.Equals(change.RecordType, "TXT", StringComparison.OrdinalIgnoreCase)
            ? $"\"{change.DesiredValue.Replace("\"", "\\\"")}\""
            : change.DesiredValue;

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);
    private sealed record ApiResponse<T>([property: JsonPropertyName("result")] T? Result);
    private sealed record IdRecord([property: JsonPropertyName("id")] string Id);
    private sealed record DnsRecordPayload(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("content")] string Content);
}
