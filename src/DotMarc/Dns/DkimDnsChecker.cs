using DotMarc.Data;
using DotMarc.DnsPush;

namespace DotMarc.Dns;

/// <summary>Checks DKIM selector record(s) at &lt;selector&gt;._domainkey.&lt;domain&gt;. Unlike
/// every other checker in this feature, this one is opt-in and takes the caller-supplied selector
/// list directly - dotMARC has no way to discover a domain's DKIM selector(s) on its own (they are
/// provider-specific strings with no DNS-discoverable convention), so this never guesses.</summary>
public sealed class DkimDnsChecker : IDkimDnsChecker
{
    private readonly TxtRecordLookup _lookup;

    public DkimDnsChecker(HttpClient http) => _lookup = new TxtRecordLookup(http);

    public async Task<DkimCheckResult> CheckAsync(string domainName, IReadOnlyList<string> selectors, CancellationToken cancellationToken, IReadOnlyList<DkimExpectedRecord>? expectedRecords = null, bool expectsNoSenders = false)
    {
        if (selectors.Count == 0)
            return new DkimCheckResult(DkimCheckStatus.NotConfigured, null);
        var missing = new List<string>();
        var keyNotPublished = new List<string>();
        var noKey = new List<string>();
        var mismatches = new List<string>();
        var unexpectedKeys = new List<string>();

        foreach (var selector in selectors)
        {
            var live = await LookupAsync($"{selector}._domainkey.{domainName}", cancellationToken).ConfigureAwait(false);
            var expected = expectedRecords?.FirstOrDefault(record => string.Equals(record.Selector, selector, StringComparison.OrdinalIgnoreCase));
            var cname = live.DelegatedToCname?.TrimEnd('.');

            if (expectsNoSenders)
            {
                var values = live.TxtValues.Count > 0 ? live.TxtValues : live.DirectValue is { } value ? [value] : [];
                if (values.Any(value => !string.IsNullOrEmpty(DkimRecordValue.PublicKey(value))))
                    unexpectedKeys.Add(selector);
                else if (values.Count > 0 && (values.Count != 1 || !DkimRecordValue.IsRevoked(values[0])))
                    noKey.Add(selector);
                continue;
            }

            switch (expected?.Type)
            {
                case null when live.DirectValue is null:
                    missing.Add(selector);
                    break;
                case null when string.IsNullOrEmpty(DkimRecordValue.PublicKey(live.DirectValue!)):
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

        if (unexpectedKeys.Count > 0)
            return new DkimCheckResult(DkimCheckStatus.UnexpectedSendingKey,
                $"Null SPF declares no senders, but selector(s) {string.Join(", ", unexpectedKeys)} still publish a DKIM public key. Remove or revoke unused keys; do not publish replacement sending keys.");

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
                parts.Insert(0, expectsNoSenders
                    ? $"Selector(s) do not contain an unambiguous revoked key (empty p=): {string.Join(", ", noKey)}"
                    : $"Selector(s) missing a usable p= public key (absent or revoked): {string.Join(", ", noKey)}");
            }

            return new DkimCheckResult(DkimCheckStatus.Misconfigured, string.Join("; ", parts));
        }

        return expectsNoSenders
            ? new DkimCheckResult(DkimCheckStatus.NoSendingKeys, "Configured selectors have no published key or an explicitly revoked key (empty p=). Other selectors have not been enumerated.")
            : new DkimCheckResult(DkimCheckStatus.Ok, null);
    }

    public Task<DnsRecordLookupResult> LookupSelectorAsync(string domainName, string selector, CancellationToken cancellationToken) =>
        LookupAsync($"{selector}._domainkey.{domainName}", cancellationToken);

    private Task<DnsRecordLookupResult> LookupAsync(string name, CancellationToken cancellationToken) =>
        _lookup.LookupWithCnameAsync(name, cancellationToken);
}
