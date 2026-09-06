namespace DotMarc.Dns;

public interface IDkimDnsChecker
{
    Task<DkimCheckResult> CheckAsync(string domainName, IReadOnlyList<string> selectors, CancellationToken cancellationToken);
}
