using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.DnsPush;

/// <summary>Fetches the raw, currently-live _dmarc.&lt;domain&gt; TXT record value — used only by
/// the DMARC push flow, to decide Create vs. Merge and build the merged value against whatever's
/// live right now. Mirrors DmarcDnsChecker's own TXT-fetching logic rather than sharing code with
/// it, matching this codebase's existing MxHostsLookup/MtaStsDnsVerifier precedent of small,
/// independent DNS-over-HTTPS callers over a shared abstraction.</summary>
public sealed class DmarcTxtLookup : IDmarcTxtLookup
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public DmarcTxtLookup(HttpClient http) => _http = http;

    public async Task<string?> LookupAsync(string domainName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString($"_dmarc.{domainName}")}&type=TXT");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;

        var answer = parsed.Answer?.FirstOrDefault(a => a.Type == 16);
        return answer is null ? null : string.Join("", answer.Data.Split("\" \"")).Trim('"');
    }

    private sealed record DnsOverHttpsResponse(
        [property: JsonPropertyName("Status")] int Status,
        [property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer(
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("data")] string Data);
}
