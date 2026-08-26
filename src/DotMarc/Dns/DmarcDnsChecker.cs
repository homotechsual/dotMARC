using System.Text.Json;
using System.Text.Json.Serialization;
using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>Checks whether a domain's DMARC records are correctly in place, querying Cloudflare's
/// DNS-over-HTTPS JSON API rather than whatever resolver the host happens to have configured — see
/// docs/superpowers/specs/2026-08-26-dmarc-dns-status-design.md for why. A waterfall, not two
/// independent lookups: each step only runs if the previous one passed, so a domain with no DMARC
/// record at all costs one query, not two.</summary>
public sealed class DmarcDnsChecker : IDmarcDnsChecker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public DmarcDnsChecker(HttpClient http) => _http = http;

    public async Task<DmarcCheckResult> CheckAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken)
    {
        var ownRecord = await QueryTxtAsync($"_dmarc.{domainName}", cancellationToken).ConfigureAwait(false);
        if (ownRecord is null)
        {
            return new DmarcCheckResult(DmarcCheckStatus.MissingOwnRecord, $"No TXT record found at _dmarc.{domainName}");
        }

        if (!ownRecord.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase))
        {
            return new DmarcCheckResult(DmarcCheckStatus.Misconfigured, $"_dmarc.{domainName} does not start with v=DMARC1: {ownRecord}");
        }

        var ruaAddresses = ParseRuaAddresses(ownRecord);
        if (!ruaAddresses.Any(a => string.Equals(a, mailboxAddress, StringComparison.OrdinalIgnoreCase)))
        {
            return new DmarcCheckResult(DmarcCheckStatus.Misconfigured,
                ruaAddresses.Count == 0
                    ? $"_dmarc.{domainName} has no rua= tag"
                    : $"_dmarc.{domainName}'s rua= points to {string.Join(", ", ruaAddresses)}, not {mailboxAddress}");
        }

        var mailboxDomain = mailboxAddress[(mailboxAddress.IndexOf('@') + 1)..];
        if (string.Equals(mailboxDomain, domainName, StringComparison.OrdinalIgnoreCase))
        {
            return new DmarcCheckResult(DmarcCheckStatus.Ok, null);
        }

        var authorizationName = $"{domainName}._report._dmarc.{mailboxDomain}";
        var authorizationRecord = await QueryTxtAsync(authorizationName, cancellationToken).ConfigureAwait(false);
        return authorizationRecord is null
            ? new DmarcCheckResult(DmarcCheckStatus.MissingAuthorizationRecord, $"No TXT record found at {authorizationName}")
            : new DmarcCheckResult(DmarcCheckStatus.Ok, null);
    }

    /// <summary>Returns the first TXT record's value (quotes stripped, multi-segment values
    /// joined), or null if the name doesn't resolve or has no TXT records (Cloudflare's JSON API
    /// omits Answer entirely for both NXDOMAIN and NODATA — no need to branch on Status).</summary>
    private async Task<string?> QueryTxtAsync(string name, CancellationToken cancellationToken)
    {
        var response = await _http.GetAsync($"dns-query?name={Uri.EscapeDataString(name)}&type=TXT", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;

        var answer = parsed.Answer?.FirstOrDefault();
        if (answer is null)
        {
            return null;
        }

        // Cloudflare's JSON API returns the TXT record's data as one or more double-quoted
        // segments (multiple only for a value over 255 bytes, split across DNS's own
        // character-string length limit) — e.g. "\"v=DMARC1; p=quarantine\"" for a short record,
        // or "\"first part\" \"second part\"" for a long one. Splitting on `" "` between quoted
        // segments and stripping the outer quotes from what's left reconstructs the original value.
        return string.Join("", answer.Data.Split("\" \"")).Trim('"');
    }

    private static List<string> ParseRuaAddresses(string record)
    {
        var ruaTag = record.Split(';')
            .Select(part => part.Trim())
            .FirstOrDefault(part => part.StartsWith("rua=", StringComparison.OrdinalIgnoreCase));

        if (ruaTag is null)
        {
            return [];
        }

        return ruaTag["rua=".Length..]
            .Split(',')
            .Select(uri => uri.Trim())
            .Where(uri => uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            .Select(uri => uri["mailto:".Length..])
            .ToList();
    }

    private sealed record DnsOverHttpsResponse(
        [property: JsonPropertyName("Status")] int Status,
        [property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("data")] string Data);
}
