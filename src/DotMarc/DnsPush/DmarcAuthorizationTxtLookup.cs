using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.DnsPush;

/// <summary>Fetches the raw, currently-live value at a DMARC authorization record's name — used
/// only by the "push authorization record" flow, to decide Create vs. Merge/Replace before pushing.
/// Mirrors DmarcTxtLookup/TlsrptTxtLookup's own DNS-over-HTTPS querying, just against a
/// caller-supplied name instead of a domain-plus-fixed-prefix (see IDmarcAuthorizationTxtLookup's
/// remarks for why).</summary>
public sealed class DmarcAuthorizationTxtLookup : IDmarcAuthorizationTxtLookup
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public DmarcAuthorizationTxtLookup(HttpClient http) => _http = http;

    public async Task<DnsRecordLookupResult> LookupAsync(string recordName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(recordName)}&type=TXT");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;
        return DnsRecordLookupParsing.ParseTxtWithCnameDetection(parsed.Answer?.Select(a => (a.Type, a.Data)));
    }

    private sealed record DnsOverHttpsResponse(
        [property: JsonPropertyName("Status")] int Status,
        [property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer(
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("data")] string Data);
}
