namespace DotMarc.Dns;

public interface IDkimDnsChecker
{
    /// <summary>Checks each selector. With an expected record for a selector, DNS must also match it.</summary>
    Task<DkimCheckResult> CheckAsync(string domainName, IReadOnlyList<string> selectors, CancellationToken cancellationToken, IReadOnlyList<DkimExpectedRecord>? expectedRecords = null);
}
