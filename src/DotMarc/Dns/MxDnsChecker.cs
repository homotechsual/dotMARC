using System.Text.Json;
using System.Text.Json.Serialization;
using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>Checks MX record health at the monitored domain's apex: presence, an explicit RFC 7505
/// null MX ("0 .") counted as an intentional "this domain does not receive mail" policy rather
/// than a failure, and that every real MX target actually resolves. Does its own raw MX query
/// rather than reusing DotMarc.MtaSts.IMxHostsLookup, which trims/dedupes for a different purpose
/// (pre-filling MTA-STS policy tags) and would collapse a null MX's "." exchange to an empty
/// string, losing the distinction this checker needs to make.</summary>
public sealed class MxDnsChecker : IMxDnsChecker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public MxDnsChecker(HttpClient http) => _http = http;

    public async Task<MxCheckResult> CheckAsync(string domainName, CancellationToken cancellationToken)
    {
        var mxAnswers = await QueryMxAsync(domainName, cancellationToken).ConfigureAwait(false);

        if (mxAnswers.Count == 0)
        {
            return new MxCheckResult(MxCheckStatus.MissingRecord, $"No MX record found at {domainName}");
        }
        if (mxAnswers.Count == 1 && mxAnswers[0].Exchange == ".")
        {
            return new MxCheckResult(MxCheckStatus.Ok, "Explicit null MX (RFC 7505) — this domain intentionally does not accept mail.");
        }

        var unresolvable = new List<string>();
        foreach (var (_, exchange) in mxAnswers)
        {
            var host = exchange.TrimEnd('.');
            if (!await ResolvesAsync(host, cancellationToken).ConfigureAwait(false))
            {
                unresolvable.Add(host);
            }
        }

        return unresolvable.Count > 0
            ? new MxCheckResult(MxCheckStatus.UnresolvableTarget, $"MX target(s) do not resolve: {string.Join(", ", unresolvable)}")
            : new MxCheckResult(MxCheckStatus.Ok, null);
    }

    private async Task<List<(int Preference, string Exchange)>> QueryMxAsync(string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type=MX");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;
        return (parsed.Answer ?? [])
            .Where(a => a.Type == 15)
            .Select(a =>
            {
                var parts = a.Data.Split(' ', 2);
                return (Preference: int.Parse(parts[0]), Exchange: parts[1]);
            })
            .ToList();
    }

    private async Task<bool> ResolvesAsync(string host, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(host)}&type=A");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;
        return (parsed.Answer ?? []).Any(a => a.Type == 1);
    }

    private sealed record DnsOverHttpsResponse([property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
