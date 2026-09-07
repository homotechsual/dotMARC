using System.Text.Json;
using System.Text.Json.Serialization;
using DotMarc.Data;

namespace DotMarc.DnsPush;

/// <summary>Detects whether a domain's DNS is hosted on Cloudflare or Azure DNS, and which zone
/// actually owns its records. Queries NS at the given hostname and, if that returns no NS records
/// (the hostname is a plain name inside a parent zone, not a delegated zone of its own - the normal
/// shape for a monitored subdomain like "services.wrc.wales" whose real zone is "wrc.wales"), walks
/// up one label at a time until an ancestor with real NS records is found. That ancestor IS the zone
/// apex - NS records only ever exist exactly at a zone cut, regardless of provider, so this needs no
/// authenticated API access to either provider just to find the answer. Capped at 6 total queries
/// (the original hostname plus up to 5 ancestors) to bound worst-case query count against a
/// pathological input; a real monitored domain resolves within 1-2 queries. Queries Cloudflare's own
/// DNS-over-HTTPS JSON API, same approach as DmarcDnsChecker/MtaStsDnsVerifier.</summary>
public sealed class DnsProviderDetector : IDnsProviderDetector
{
    private const int MaxQueries = 6;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] CloudflareNsSuffixes = [".ns.cloudflare.com"];
    private static readonly string[] AzureDnsNsSuffixes =
        [".azure-dns.com", ".azure-dns.net", ".azure-dns.org", ".azure-dns.info"];
    // Also the NS suffix the legacy Google Domains registrar (now Squarespace-operated) used and
    // still uses for unmigrated zones - there is no way to tell "a real Cloud DNS zone this app
    // can push to" apart from "a legacy Google Domains zone with no GCP project behind it" using
    // NS records alone. A push against the latter just fails cleanly as ZoneNotFound once zone
    // discovery searches every accessible project and finds nothing, same as any other unmatched
    // zone - accepted, not fixable without a different kind of signal than NS suffix matching.
    private static readonly string[] GoogleCloudDnsNsSuffixes = [".googledomains.com"];

    private readonly HttpClient _http;

    public DnsProviderDetector(HttpClient http) => _http = http;

    public async Task<DnsProviderDetectionResult> DetectAsync(string domainName, CancellationToken cancellationToken)
    {
        var candidate = domainName;
        for (var i = 0; i < MaxQueries; i++)
        {
            var nsHosts = await QueryNsHostsAsync(candidate, cancellationToken).ConfigureAwait(false);

            if (nsHosts.Count > 0)
            {
                foreach (var host in nsHosts)
                {
                    if (CloudflareNsSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                    {
                        return new DnsProviderDetectionResult(DetectedDnsProvider.Cloudflare, candidate);
                    }
                    if (AzureDnsNsSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                    {
                        return new DnsProviderDetectionResult(DetectedDnsProvider.AzureDns, candidate);
                    }
                    if (GoogleCloudDnsNsSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                    {
                        return new DnsProviderDetectionResult(DetectedDnsProvider.GoogleCloudDns, candidate);
                    }
                }

                // NS records exist at this candidate but match no known provider - this IS the
                // zone (a real delegation, just to an unrecognized nameserver), so stop walking
                // rather than treating it as "not delegated yet" and searching further up.
                return new DnsProviderDetectionResult(DetectedDnsProvider.Unknown, candidate);
            }

            var nextDot = candidate.IndexOf('.');
            if (nextDot < 0)
            {
                break;
            }
            candidate = candidate[(nextDot + 1)..];
        }

        return new DnsProviderDetectionResult(DetectedDnsProvider.Unknown, domainName);
    }

    private async Task<List<string>> QueryNsHostsAsync(string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type=NS");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;

        return (parsed.Answer ?? []).Where(a => a.Type == 2).Select(a => a.Data.TrimEnd('.')).ToList();
    }

    private sealed record DnsOverHttpsResponse(
        [property: JsonPropertyName("Status")] int Status,
        [property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer(
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("data")] string Data);
}
