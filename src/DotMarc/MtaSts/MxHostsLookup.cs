using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.MtaSts;

/// <summary>Looks up a domain's current MX records, for pre-filling the MX hosts field on Manage
/// MTA-STS rather than requiring an admin to type them from memory — a receiving server rejects
/// the MTA-STS-protected connection if the live MX record points somewhere not on that list, so
/// getting it wrong silently breaks mail delivery. Queries Cloudflare's DNS-over-HTTPS JSON API,
/// same approach and same reasoning as DmarcDnsChecker/MtaStsDnsVerifier.</summary>
public sealed class MxHostsLookup : IMxHostsLookup
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public MxHostsLookup(HttpClient http) => _http = http;

    public async Task<List<string>> LookupAsync(string domainName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(domainName)}&type=MX");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;

        var mxAnswers = parsed.Answer?.Where(a => a.Type == 15) ?? [];

        // MX record data is "<preference> <exchange>" (e.g. "10 mail.contoso.io."), lower
        // preference meaning higher priority — sorted so the primary server ends up first in the
        // populated field rather than in whatever order DNS happened to return them.
        return mxAnswers
            .Select(ParsePreferenceAndExchange)
            .OrderBy(pair => pair.Preference)
            .Select(pair => pair.Exchange)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static (int Preference, string Exchange) ParsePreferenceAndExchange(DnsAnswer answer)
    {
        var parts = answer.Data.Split(' ', 2);
        var preference = int.Parse(parts[0]);
        var exchange = parts[1].TrimEnd('.');
        return (preference, exchange);
    }

    private sealed record DnsOverHttpsResponse(
        [property: JsonPropertyName("Status")] int Status,
        [property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer(
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("data")] string Data);
}
