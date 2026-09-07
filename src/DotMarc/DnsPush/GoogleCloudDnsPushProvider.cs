using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.DnsPush;

/// <summary>Pushes a DNS record change to Google Cloud DNS, authenticated via a fresh OAuth 2.0
/// Authorization Code + PKCE exchange each time - same "nothing about the end-user's push-time
/// token is ever persisted" contract as CloudflareDnsPushProvider/AzureDnsPushProvider. Unlike
/// those two, Google Cloud DNS has no direct "find the zone matching this domain name" lookup -
/// zones live inside GCP Projects, so finding the right one means enumerating every project the
/// authorizing user can see (Cloud Resource Manager API) and checking each one's zones - see
/// FindZoneAsync. Every mutation goes through Cloud DNS's Change resource
/// (https://cloud.google.com/dns/docs/reference/v1/changes), which applies atomically - unlike
/// Cloudflare/Azure's separate delete-then-create for a Replace, there is no window where the old
/// record is gone and the new one hasn't landed yet, so ReplaceRecordAsync never needs
/// DnsPushOutcome.ReplaceFailedAfterDelete: a failed Change call means nothing changed, full
/// stop.</summary>
public sealed class GoogleCloudDnsPushProvider : IDnsPushProvider
{
    private const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string ResourceManagerBase = "https://cloudresourcemanager.googleapis.com/v1";
    private const string DnsApiBase = "https://dns.googleapis.com/dns/v1";

    private readonly IDbContextFactory<DotMarcDbContext> _dbFactory;
    private readonly ISecretStore _secretStore;
    private readonly HttpClient _http;

    public GoogleCloudDnsPushProvider(IDbContextFactory<DotMarcDbContext> dbFactory, ISecretStore secretStore, HttpClient http)
    {
        _dbFactory = dbFactory;
        _secretStore = secretStore;
        _http = http;
    }

    public string ProviderKey => "google-cloud-dns";

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
            ["client_id"] = settings.ClientId!,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            // ndev.clouddns.readwrite for the actual record push; cloudplatformprojects.readonly
            // (the narrowest scope Resource Manager's projects.list accepts) for FindZoneAsync's
            // project enumeration below. No access_type/prompt=consent - this flow never requests
            // or needs a refresh token, same as Cloudflare/Azure.
            ["scope"] = "https://www.googleapis.com/auth/ndev.clouddns.readwrite https://www.googleapis.com/auth/cloudplatformprojects.readonly",
            // Without this, a browser with an active Google session silently reuses it - same
            // reasoning as AzureDnsPushProvider's prompt=select_account.
            ["prompt"] = "select_account",
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
        var clientSecret = await _secretStore.GetSecretAsync(GoogleCloudDnsSettings.SecretStoreKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(settings.ClientId) || string.IsNullOrEmpty(clientSecret))
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, "Google Cloud DNS push is not configured for this deployment.");
        }

        // Everything below is network calls: token exchange, then for each change a
        // projects.list/managedZones.list zone search, an rrsets lookup, and a changes POST.
        // Unlike Cloudflare/Azure - which need narrower, carefully placed catches around their
        // non-atomic delete-then-create window so a mid-operation failure can be reported as
        // ReplaceFailedAfterDelete rather than a plain error - every mutation here goes through
        // Cloud DNS's atomic Change API, so no matter which call below fails, nothing was ever
        // half-applied. That means one outer catch is sufficient: there is no partial-failure
        // state to distinguish.
        try
        {
            var accessToken = await ExchangeCodeForTokenAsync(settings.ClientId, clientSecret, code, codeVerifier, redirectUri, cancellationToken).ConfigureAwait(false);
            if (accessToken is null)
            {
                return new DnsPushResult(DnsPushOutcome.ProviderError, "Google rejected the authorization code exchange.");
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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Couldn't reach Google to push the DNS record: {ex.Message} - nothing was changed.");
        }
    }

    private async Task<DnsPushResult> PushOneChangeAsync(DnsRecordChange change, string accessToken, CancellationToken cancellationToken)
    {
        var (projectId, managedZoneName, errorStatus) = await FindZoneAsync(change.ZoneName, accessToken, cancellationToken).ConfigureAwait(false);
        if (errorStatus.HasValue)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Google rejected the project/zone lookup ({errorStatus}).");
        }
        if (projectId is null || managedZoneName is null)
        {
            return new DnsPushResult(DnsPushOutcome.ZoneNotFound, $"Couldn't find {change.ZoneName} in any Google Cloud project you authorized.");
        }

        return change.Kind switch
        {
            DnsRecordChangeKind.Merge => await UpdateExistingRecordAsync(projectId, managedZoneName, accessToken, change, cancellationToken).ConfigureAwait(false),
            DnsRecordChangeKind.Replace => await ReplaceRecordAsync(projectId, managedZoneName, accessToken, change, cancellationToken).ConfigureAwait(false),
            _ => await CreateRecordAsync(projectId, managedZoneName, accessToken, change, cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<GoogleCloudDnsSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await GoogleCloudDnsSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Google Cloud DNS has no "find the zone matching this domain name" lookup the way
    /// Cloudflare's GET /zones?name=... or Azure's per-subscription zone enumeration does - zones
    /// live inside GCP Projects, so this enumerates every project the authorizing user can see
    /// (Resource Manager's projects.list, paginated) and checks each one's managed zones (Cloud
    /// DNS's managedZones.list, also paginated) for a dnsName match. First match across the whole
    /// search wins, same as AzureDnsPushProvider.FindZoneAsync's first-subscription-first-zone-wins
    /// behavior.</summary>
    private async Task<(string? ProjectId, string? ManagedZoneName, int? ErrorStatusCode)> FindZoneAsync(string zoneName, string accessToken, CancellationToken cancellationToken)
    {
        var targetDnsName = zoneName.TrimEnd('.') + ".";

        string? projectsPageToken = null;
        do
        {
            var projectsUrl = $"{ResourceManagerBase}/projects" + (projectsPageToken is null ? "" : $"?pageToken={Uri.EscapeDataString(projectsPageToken)}");
            using var projectsRequest = new HttpRequestMessage(HttpMethod.Get, projectsUrl);
            projectsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var projectsResponse = await _http.SendAsync(projectsRequest, cancellationToken).ConfigureAwait(false);
            if (!projectsResponse.IsSuccessStatusCode)
            {
                return (null, null, (int)projectsResponse.StatusCode);
            }
            var projectsPage = await projectsResponse.Content.ReadFromJsonAsync<ProjectsListResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var project in projectsPage?.Projects ?? [])
            {
                var managedZoneName = await FindManagedZoneAsync(project.ProjectId, targetDnsName, accessToken, cancellationToken).ConfigureAwait(false);
                if (managedZoneName is not null)
                {
                    return (project.ProjectId, managedZoneName, null);
                }
            }

            projectsPageToken = projectsPage?.NextPageToken;
        } while (!string.IsNullOrEmpty(projectsPageToken));

        return (null, null, null);
    }

    private async Task<string?> FindManagedZoneAsync(string projectId, string targetDnsName, string accessToken, CancellationToken cancellationToken)
    {
        string? zonesPageToken = null;
        do
        {
            var zonesUrl = $"{DnsApiBase}/projects/{projectId}/managedZones" + (zonesPageToken is null ? "" : $"?pageToken={Uri.EscapeDataString(zonesPageToken)}");
            using var zonesRequest = new HttpRequestMessage(HttpMethod.Get, zonesUrl);
            zonesRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var zonesResponse = await _http.SendAsync(zonesRequest, cancellationToken).ConfigureAwait(false);
            if (!zonesResponse.IsSuccessStatusCode)
            {
                // A project the caller can list but not query Cloud DNS in (API not enabled, no
                // dns.viewer role there, etc.) - skip it and keep searching other projects rather
                // than failing the whole search over one inaccessible project.
                return null;
            }
            var zonesPage = await zonesResponse.Content.ReadFromJsonAsync<ManagedZonesListResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);

            var match = zonesPage?.ManagedZones?.FirstOrDefault(z => string.Equals(z.DnsName, targetDnsName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match.Name;
            }

            zonesPageToken = zonesPage?.NextPageToken;
        } while (!string.IsNullOrEmpty(zonesPageToken));

        return null;
    }

    private async Task<DnsPushResult> CreateRecordAsync(string projectId, string managedZoneName, string accessToken, DnsRecordChange change, CancellationToken cancellationToken)
    {
        var fqdn = change.Name.TrimEnd('.') + ".";
        var existing = await GetExistingRrsetAsync(projectId, managedZoneName, fqdn, change.RecordType, accessToken, cancellationToken).ConfigureAwait(false);
        if (existing.ErrorStatusCode.HasValue)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Google rejected the record lookup ({existing.ErrorStatusCode}).");
        }
        if (existing.Rrset is not null)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"A DNS record already exists at {change.Name} - remove it or update it manually rather than risk overwriting it.");
        }

        var newRrset = new ResourceRecordSet(fqdn, change.RecordType, 3600, [BuildRrdata(change)]);
        return await ApplyChangeAsync(projectId, managedZoneName, accessToken, additions: [newRrset], deletions: [], cancellationToken).ConfigureAwait(false);
    }

    private async Task<DnsPushResult> UpdateExistingRecordAsync(string projectId, string managedZoneName, string accessToken, DnsRecordChange change, CancellationToken cancellationToken)
    {
        var fqdn = change.Name.TrimEnd('.') + ".";
        var existing = await GetExistingRrsetAsync(projectId, managedZoneName, fqdn, change.RecordType, accessToken, cancellationToken).ConfigureAwait(false);
        if (existing.ErrorStatusCode.HasValue)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Google rejected the record lookup ({existing.ErrorStatusCode}).");
        }
        if (existing.Rrset is null)
        {
            return new DnsPushResult(DnsPushOutcome.ZoneNotFound, $"{change.Name} no longer exists at Google Cloud DNS - it may have been removed since this page loaded.");
        }

        var newRrset = new ResourceRecordSet(fqdn, change.RecordType, 3600, [BuildRrdata(change)]);
        return await ApplyChangeAsync(projectId, managedZoneName, accessToken, additions: [newRrset], deletions: [existing.Rrset], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes whatever record of change.ExistingRecordType currently exists at
    /// change.Name and creates a change.RecordType record with change.DesiredValue in its place,
    /// in ONE atomic Cloud DNS Change call - unlike CloudflareDnsPushProvider/AzureDnsPushProvider's
    /// separate delete-then-create, there is no window where the deletion has landed but the
    /// creation hasn't, so a failure here always means nothing changed
    /// (DnsPushOutcome.ProviderError, never ReplaceFailedAfterDelete - that outcome describes a
    /// partial-failure state this provider's atomic Change API cannot produce).</summary>
    private async Task<DnsPushResult> ReplaceRecordAsync(string projectId, string managedZoneName, string accessToken, DnsRecordChange change, CancellationToken cancellationToken)
    {
        var existingType = change.ExistingRecordType ?? change.RecordType;
        var fqdn = change.Name.TrimEnd('.') + ".";

        var existing = await GetExistingRrsetAsync(projectId, managedZoneName, fqdn, existingType, accessToken, cancellationToken).ConfigureAwait(false);
        if (existing.ErrorStatusCode.HasValue)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Google rejected the record lookup ({existing.ErrorStatusCode}) - nothing was changed.");
        }
        if (existing.Rrset is null)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"The {existingType} record at {change.Name} no longer exists at Google Cloud DNS - it may have been removed since this page loaded. Nothing was changed; try again.");
        }

        var newRrset = new ResourceRecordSet(fqdn, change.RecordType, 3600, [BuildRrdata(change)]);
        var result = await ApplyChangeAsync(projectId, managedZoneName, accessToken, additions: [newRrset], deletions: [existing.Rrset], cancellationToken).ConfigureAwait(false);
        return result.Outcome == DnsPushOutcome.Pushed
            ? result
            : new DnsPushResult(DnsPushOutcome.ProviderError, $"{result.DetailMessage} Nothing was changed - Google applies this kind of change atomically, so a failed request never leaves {change.Name} without a record.");
    }

    private async Task<(ResourceRecordSet? Rrset, int? ErrorStatusCode)> GetExistingRrsetAsync(string projectId, string managedZoneName, string fqdn, string recordType, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{DnsApiBase}/projects/{projectId}/managedZones/{managedZoneName}/rrsets?name={Uri.EscapeDataString(fqdn)}&type={Uri.EscapeDataString(recordType)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (null, (int)response.StatusCode);
        }
        var page = await response.Content.ReadFromJsonAsync<RrsetsListResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return (page?.Rrsets?.FirstOrDefault(), null);
    }

    private async Task<DnsPushResult> ApplyChangeAsync(string projectId, string managedZoneName, string accessToken, List<ResourceRecordSet> additions, List<ResourceRecordSet> deletions, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{DnsApiBase}/projects/{projectId}/managedZones/{managedZoneName}/changes")
        {
            Content = JsonContent.Create(new ChangeRequest(additions, deletions))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? new DnsPushResult(DnsPushOutcome.Pushed, null)
            : new DnsPushResult(DnsPushOutcome.ProviderError, $"Google rejected the record push ({(int)response.StatusCode}).");
    }

    /// <summary>Cloud DNS's rrdatas entries are literal zone-file text, so a TXT value must be
    /// quoted (an unquoted value is non-conformant) - same reasoning as
    /// CloudflareDnsPushProvider.BuildContent. Every value this app pushes (DMARC/TLSRPT policy
    /// text, the MTA-STS asuid verification token) is plain text with no embedded quotes, so the
    /// escape only guards against a value that happens to contain one. CNAME (and any other
    /// non-TXT type) content is never zone-file text and must NOT be quoted.</summary>
    private static string BuildRrdata(DnsRecordChange change) =>
        string.Equals(change.RecordType, "TXT", StringComparison.OrdinalIgnoreCase)
            ? $"\"{change.DesiredValue.Replace("\"", "\\\"")}\""
            : change.DesiredValue;

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);
    private sealed record ProjectsListResponse(
        [property: JsonPropertyName("projects")] List<ProjectEntry>? Projects,
        [property: JsonPropertyName("nextPageToken")] string? NextPageToken);
    private sealed record ProjectEntry([property: JsonPropertyName("projectId")] string ProjectId);
    private sealed record ManagedZonesListResponse(
        [property: JsonPropertyName("managedZones")] List<ManagedZoneEntry>? ManagedZones,
        [property: JsonPropertyName("nextPageToken")] string? NextPageToken);
    private sealed record ManagedZoneEntry(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("dnsName")] string DnsName);
    private sealed record RrsetsListResponse([property: JsonPropertyName("rrsets")] List<ResourceRecordSet>? Rrsets);
    private sealed record ResourceRecordSet(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("ttl")] int Ttl,
        [property: JsonPropertyName("rrdatas")] List<string> Rrdatas);
    private sealed record ChangeRequest(
        [property: JsonPropertyName("additions")] List<ResourceRecordSet> Additions,
        [property: JsonPropertyName("deletions")] List<ResourceRecordSet> Deletions);
}
