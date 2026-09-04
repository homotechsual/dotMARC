using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.MtaSts;

/// <summary>Fetches the raw, currently-live mta-sts.&lt;domain&gt; CNAME target — used by the
/// MTA-STS push flow to decide Create vs. Merge before pushing, the same way
/// DmarcTxtLookup/TlsrptTxtLookup already do for their record types. A CNAME here is MTA-STS's own
/// normal, expected record type (unlike DMARC/TLSRPT, where finding one instead of a plain TXT
/// record means third-party delegation) — there is no delegation concept for this lookup.</summary>
public sealed class MtaStsCnameLookup : IMtaStsCnameLookup
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public MtaStsCnameLookup(HttpClient http) => _http = http;

    public async Task<string?> LookupAsync(string domainName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString($"mta-sts.{domainName}")}&type=CNAME");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;
        var answer = parsed.Answer?.FirstOrDefault(a => a.Type == 5);
        return answer?.Data.TrimEnd('.');
    }

    private sealed record DnsOverHttpsResponse([property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
