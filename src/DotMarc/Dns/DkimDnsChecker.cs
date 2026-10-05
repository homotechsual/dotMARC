using System.Text.Json;
using System.Text.Json.Serialization;
using DotMarc.Data;
using DotMarc.DnsPush;

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

    public async Task<DkimCheckResult> CheckAsync(string domainName, IReadOnlyList<string> selectors, CancellationToken cancellationToken, IReadOnlyList<DkimExpectedRecord>? expectedRecords = null)
    {
        var missing = new List<string>();
        var keyNotPublished = new List<string>();
        var noKey = new List<string>();
        var mismatches = new List<string>();

        foreach (var selector in selectors)
        {
            var live = await LookupAsync($"{selector}._domainkey.{domainName}", cancellationToken).ConfigureAwait(false);
            var expected = expectedRecords?.FirstOrDefault(record => string.Equals(record.Selector, selector, StringComparison.OrdinalIgnoreCase));
            var cname = live.DelegatedToCname?.TrimEnd('.');

            switch (expected?.Type)
            {
                case null when live.DirectValue is null:
                    missing.Add(selector);
                    break;
                case null when !live.DirectValue!.Contains("p=", StringComparison.OrdinalIgnoreCase):
                    noKey.Add(selector);
                    break;
                case DkimRecordType.Cname when cname is null && live.DirectValue is null:
                    missing.Add(selector);
                    break;
                case DkimRecordType.Cname when cname is null:
                    mismatches.Add($"{selector} is a TXT record, but a CNAME to {expected.Value} is expected");
                    break;
                case DkimRecordType.Cname when !string.Equals(cname, expected.Value, StringComparison.OrdinalIgnoreCase):
                    mismatches.Add($"{selector} points to {cname}, expected {expected.Value}");
                    break;
                case DkimRecordType.Cname when string.IsNullOrEmpty(DkimRecordValue.PublicKey(live.DirectValue ?? "")):
                    keyNotPublished.Add(selector);
                    break;
                case DkimRecordType.Txt when live.DirectValue is null:
                    missing.Add(selector);
                    break;
                case DkimRecordType.Txt when DkimRecordValue.PublicKey(live.DirectValue!) != DkimRecordValue.PublicKey(expected.Value):
                    mismatches.Add($"{selector}'s key doesn't match the one stored in dotMARC");
                    break;
            }
        }

        if (missing.Count > 0 || keyNotPublished.Count > 0)
        {
            var parts = new List<string>();
            if (missing.Count > 0)
            {
                parts.Add($"No DKIM record found for selector(s): {string.Join(", ", missing)}.");
            }

            if (keyNotPublished.Count > 0)
            {
                parts.Add($"The CNAME for selector(s) {string.Join(", ", keyNotPublished)} is in place, but the mail platform hasn't published the key yet: turn on DKIM signing there.");
            }

            return new DkimCheckResult(DkimCheckStatus.Missing, string.Join(' ', parts));
        }

        if (noKey.Count > 0 || mismatches.Count > 0)
        {
            var parts = new List<string>(mismatches);
            if (noKey.Count > 0)
            {
                parts.Insert(0, $"Selector(s) missing a p= public-key tag: {string.Join(", ", noKey)}");
            }

            return new DkimCheckResult(DkimCheckStatus.Misconfigured, string.Join("; ", parts));
        }

        return new DkimCheckResult(DkimCheckStatus.Ok, null);
    }

    public Task<DnsRecordLookupResult> LookupSelectorAsync(string domainName, string selector, CancellationToken cancellationToken) =>
        LookupAsync($"{selector}._domainkey.{domainName}", cancellationToken);

    private async Task<DnsRecordLookupResult> LookupAsync(string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type=TXT");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;
        return DnsRecordLookupParsing.ParseTxtWithCnameDetection(parsed.Answer?.Select(answer => (answer.Type, answer.Data)));
    }

    private sealed record DnsOverHttpsResponse([property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
