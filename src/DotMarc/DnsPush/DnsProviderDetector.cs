using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.DnsPush;

/// <summary>Detects whether a domain's DNS is hosted on Cloudflare or Azure DNS by matching its NS
/// records' hostnames against each provider's well-known name server suffixes. Queries Cloudflare's
/// own DNS-over-HTTPS JSON API, same approach as DmarcDnsChecker/MtaStsDnsVerifier.</summary>
public sealed class DnsProviderDetector : IDnsProviderDetector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] CloudflareNsSuffixes = [".ns.cloudflare.com"];
    private static readonly string[] AzureDnsNsSuffixes =
        [".azure-dns.com", ".azure-dns.net", ".azure-dns.org", ".azure-dns.info"];

    private readonly HttpClient _http;

    public DnsProviderDetector(HttpClient http) => _http = http;

    public async Task<DetectedDnsProvider> DetectAsync(string domainName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(domainName)}&type=NS");
        request.Headers.Accept.ParseAdd("application/dns-json");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException)
        {
            return DetectedDnsProvider.Unknown;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;

        var nsHosts = (parsed.Answer ?? []).Where(a => a.Type == 2).Select(a => a.Data.TrimEnd('.'));

        foreach (var host in nsHosts)
        {
            if (CloudflareNsSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                return DetectedDnsProvider.Cloudflare;
            }
            if (AzureDnsNsSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                return DetectedDnsProvider.AzureDns;
            }
        }

        return DetectedDnsProvider.Unknown;
    }

    private sealed record DnsOverHttpsResponse(
        [property: JsonPropertyName("Status")] int Status,
        [property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer(
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("data")] string Data);
}
