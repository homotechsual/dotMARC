using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.MtaSts;

/// <summary>Confirms a domain's mta-sts.&lt;domain&gt; CNAME actually points at dotMARC's own
/// hosting hostname, querying Cloudflare's DNS-over-HTTPS JSON API — same approach and same
/// reasoning as DotMarc.Dns.DmarcDnsChecker (consistent results independent of the runtime
/// environment's own resolver config, over HTTPS rather than raw UDP:53 so it works reliably from
/// Azure Container Apps).</summary>
public sealed class MtaStsDnsVerifier : IMtaStsDnsVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public MtaStsDnsVerifier(HttpClient http) => _http = http;

    public async Task<MtaStsDnsVerificationResult> VerifyAsync(string domainName, string expectedHostingHostname, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString($"mta-sts.{domainName}")}&type=CNAME");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;

        var cnameAnswers = parsed.Answer?.Where(a => a.Type == 5).ToList() ?? [];
        if (cnameAnswers.Count == 0)
        {
            return MtaStsDnsVerificationResult.NotFound;
        }

        // A canonical DNS name in the response carries a trailing dot (e.g. "dotmarc.app."); the
        // expected hostname configured for this deployment never does, so it has to be trimmed
        // before comparing.
        var pointsHere = cnameAnswers.Any(a =>
            string.Equals(a.Data.TrimEnd('.'), expectedHostingHostname.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));

        return pointsHere ? MtaStsDnsVerificationResult.Resolved : MtaStsDnsVerificationResult.PointsElsewhere;
    }

    private sealed record DnsOverHttpsResponse(
        [property: JsonPropertyName("Status")] int Status,
        [property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer(
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("data")] string Data);
}
