using System.Text.Json;
using System.Text.Json.Serialization;
using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>Checks SPF (RFC 7208) record health at the monitored domain's apex: presence, that
/// exactly one v=spf1 TXT record exists (RFC 7208 requires exactly one — multiple is a common,
/// real misconfiguration), and the v=spf1 prefix itself. Does not follow include:/redirect= chains
/// or validate the 10-DNS-lookup limit — out of scope, see the design spec's Non-goals.</summary>
public sealed class SpfDnsChecker : ISpfDnsChecker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public SpfDnsChecker(HttpClient http) => _http = http;

    public async Task<SpfCheckResult> CheckAsync(string domainName, CancellationToken cancellationToken)
    {
        var allTxtRecords = await QueryAllTxtAsync(domainName, cancellationToken).ConfigureAwait(false);
        var spfRecords = allTxtRecords.Where(r => r.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)).ToList();

        if (spfRecords.Count == 0)
        {
            // A record that mentions "spf" but doesn't start with the required "v=spf1" prefix
            // (a wrong/typo'd version tag such as "v=spf2", or a missing "v=") is a real,
            // distinguishable misconfiguration — worth telling apart from "no SPF record was
            // even attempted."
            var nearMiss = allTxtRecords.FirstOrDefault(r => r.Contains("spf", StringComparison.OrdinalIgnoreCase));
            if (nearMiss is not null)
            {
                return new SpfCheckResult(SpfCheckStatus.Misconfigured, $"{domainName} has a record that looks like SPF but doesn't start with v=spf1: {nearMiss}");
            }
            return new SpfCheckResult(SpfCheckStatus.MissingRecord, $"No SPF (v=spf1) TXT record found at {domainName}");
        }
        if (spfRecords.Count > 1)
        {
            return new SpfCheckResult(SpfCheckStatus.MultipleRecords, $"{domainName} has {spfRecords.Count} SPF records — RFC 7208 requires exactly one");
        }
        return new SpfCheckResult(SpfCheckStatus.Ok, null);
    }

    /// <summary>Unlike DmarcDnsChecker/TlsrptDnsChecker's QueryTxtAsync (which only returns the
    /// first TXT answer), this returns every TXT record at the name — detecting "multiple SPF
    /// records" requires seeing all of them, not just the first.</summary>
    private async Task<List<string>> QueryAllTxtAsync(string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type=TXT");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;
        return (parsed.Answer ?? [])
            .Where(a => a.Type == 16)
            .Select(a => string.Join("", a.Data.Split("\" \"")).Trim('"'))
            .ToList();
    }

    private sealed record DnsOverHttpsResponse([property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
