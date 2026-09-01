using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace DotMarc.DnsPush;

/// <summary>Pushes a DNS record change to Cloudflare, authenticated via a fresh OAuth 2.0
/// Authorization Code + PKCE exchange each time — see the design spec's "Auth model" section for
/// why nothing is ever persisted. Endpoints confirmed against Cloudflare's own OIDC discovery
/// document (https://dash.cloudflare.com/.well-known/openid-configuration); the DNS API itself is
/// documented at https://developers.cloudflare.com/api/resources/dns/subresources/records/.</summary>
public sealed class CloudflareDnsPushProvider : IDnsPushProvider
{
    private const string AuthorizationEndpoint = "https://dash.cloudflare.com/oauth2/auth";
    private const string TokenEndpoint = "https://dash.cloudflare.com/oauth2/token";
    private const string ApiBase = "https://api.cloudflare.com/client/v4";

    private readonly CloudflareDnsOptions _options;
    private readonly HttpClient _http;

    public CloudflareDnsPushProvider(IOptions<CloudflareDnsOptions> options, HttpClient http)
    {
        _options = options.Value;
        _http = http;
    }

    public string ProviderKey => "cloudflare";
    public bool IsConfigured => !string.IsNullOrEmpty(_options.ClientId) && !string.IsNullOrEmpty(_options.ClientSecret);

    public string BuildAuthorizationUrl(string state, string codeChallenge, string redirectUri)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = _options.ClientId!,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "com.cloudflare.api.account.zone.dns",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };
        return AuthorizationEndpoint + "?" + string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    public async Task<DnsPushResult> ExchangeAndPushAsync(
        string code, string codeVerifier, string redirectUri, DnsRecordChange change, CancellationToken cancellationToken)
    {
        var accessToken = await ExchangeCodeForTokenAsync(code, codeVerifier, redirectUri, cancellationToken).ConfigureAwait(false);
        if (accessToken is null)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, "Cloudflare rejected the authorization code exchange.");
        }

        var zoneName = ZoneNameFor(change.Name);
        var zoneId = await FindZoneIdAsync(zoneName, accessToken, cancellationToken).ConfigureAwait(false);
        if (zoneId is null)
        {
            return new DnsPushResult(DnsPushOutcome.ZoneNotFound, $"Couldn't find {zoneName} in the Cloudflare account you authorized.");
        }

        return change.Kind == DnsRecordChangeKind.Merge
            ? await UpdateExistingRecordAsync(zoneId, accessToken, change, cancellationToken).ConfigureAwait(false)
            : await CreateRecordAsync(zoneId, accessToken, change, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ExchangeCodeForTokenAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = _options.ClientId!,
                ["client_secret"] = _options.ClientSecret!,
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

    private async Task<string?> FindZoneIdAsync(string zoneName, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/zones?name={Uri.EscapeDataString(zoneName)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var zones = await response.Content.ReadFromJsonAsync<ApiResponse<List<IdRecord>>>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return zones?.Result?.FirstOrDefault()?.Id;
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

    /// <summary>dotMARC only ever calls this with a name of the form "mta-sts.&lt;domain&gt;" or
    /// "_dmarc.&lt;domain&gt;", so stripping the first label always yields the zone name — this
    /// would not generalize to arbitrary multi-label zones, and doesn't need to.</summary>
    private static string ZoneNameFor(string recordName)
    {
        var firstDot = recordName.IndexOf('.');
        return firstDot < 0 ? recordName : recordName[(firstDot + 1)..];
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);
    private sealed record ApiResponse<T>([property: JsonPropertyName("result")] T? Result);
    private sealed record IdRecord([property: JsonPropertyName("id")] string Id);
    private sealed record DnsRecordPayload(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("content")] string Content);
}
