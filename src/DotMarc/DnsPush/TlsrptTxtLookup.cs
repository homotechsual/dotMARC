using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.DnsPush;

public sealed class TlsrptTxtLookup : ITlsrptTxtLookup
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public TlsrptTxtLookup(HttpClient http) => _http = http;

    public async Task<DnsRecordLookupResult> LookupAsync(string domainName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString($"_smtp._tls.{domainName}")}&type=TXT");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;
        return DnsRecordLookupParsing.ParseTxtWithCnameDetection(parsed.Answer?.Select(a => (a.Type, a.Data)));
    }

    private sealed record DnsOverHttpsResponse([property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
