namespace DotMarc.Dns;

public interface IMxDnsChecker
{
    Task<MxCheckResult> CheckAsync(string domainName, CancellationToken cancellationToken);
}
