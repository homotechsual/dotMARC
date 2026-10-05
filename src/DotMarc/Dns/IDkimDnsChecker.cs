using DotMarc.DnsPush;

namespace DotMarc.Dns;

public interface IDkimDnsChecker
{
    /// <summary>Checks each selector. With an expected record for a selector, DNS must also match it.</summary>
    Task<DkimCheckResult> CheckAsync(string domainName, IReadOnlyList<string> selectors, CancellationToken cancellationToken, IReadOnlyList<DkimExpectedRecord>? expectedRecords = null);

    /// <summary>What one selector has published now: a CNAME target, a TXT value, or nothing.</summary>
    Task<DnsRecordLookupResult> LookupSelectorAsync(string domainName, string selector, CancellationToken cancellationToken);
}
