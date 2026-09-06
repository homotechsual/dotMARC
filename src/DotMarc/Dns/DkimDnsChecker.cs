using System.Text.Json;
using System.Text.Json.Serialization;
using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>Checks DKIM selector record(s) at &lt;selector&gt;._domainkey.&lt;domain&gt;. Unlike
/// every other checker in this feature, this one is opt-in and takes the caller-supplied selector
/// list directly - dotMARC has no way to discover a domain's DKIM selector(s) on its own (they are
/// provider-specific strings with no DNS-discoverable convention), so this never guesses.</summary>
public sealed class DkimDnsChecker : IDkimDnsChecker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public DkimDnsChecker(HttpClient http) => _http = http;

    public async Task<DkimCheckResult> CheckAsync(string domainName, IReadOnlyList<string> selectors, CancellationToken cancellationToken)
    {
        var missing = new List<string>();
        var misconfigured = new List<string>();

        foreach (var selector in selectors)
        {
            var recordName = $"{selector}._domainkey.{domainName}";
            var record = await QueryTxtAsync(recordName, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                missing.Add(selector);
            }
            else if (!record.Contains("p=", StringComparison.OrdinalIgnoreCase))
            {
                misconfigured.Add(selector);
            }
        }

        if (missing.Count > 0)
        {
            return new DkimCheckResult(DkimCheckStatus.Missing, $"No DKIM record found for selector(s): {string.Join(", ", missing)}");
        }
        if (misconfigured.Count > 0)
        {
            return new DkimCheckResult(DkimCheckStatus.Misconfigured, $"Selector(s) missing a p= public-key tag: {string.Join(", ", misconfigured)}");
        }
        return new DkimCheckResult(DkimCheckStatus.Ok, null);
    }

    private async Task<string?> QueryTxtAsync(string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type=TXT");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;
        var answer = parsed.Answer?.FirstOrDefault(a => a.Type == 16);
        return answer is null ? null : string.Join("", answer.Data.Split("\" \"")).Trim('"');
    }

    private sealed record DnsOverHttpsResponse([property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
