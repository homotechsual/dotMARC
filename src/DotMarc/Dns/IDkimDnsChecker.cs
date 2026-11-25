using DotMarc.DnsPush;

namespace DotMarc.Dns;

public interface IDkimDnsChecker
{
    /// <summary>Checks configured selectors for sending keys, or for absence/revocation when null SPF is observed.</summary>
    Task<DkimCheckResult> CheckAsync(string domainName, IReadOnlyList<string> selectors, CancellationToken cancellationToken, IReadOnlyList<DkimExpectedRecord>? expectedRecords = null, bool expectsNoSenders = false);

    /// <summary>What one selector has published now: a CNAME target, a TXT value, or nothing.</summary>
    Task<DnsRecordLookupResult> LookupSelectorAsync(string domainName, string selector, CancellationToken cancellationToken);
}
