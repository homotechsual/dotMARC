namespace DotMarc.Dns;

public interface ISpfDnsChecker
{
    Task<SpfCheckResult> CheckAsync(string domainName, CancellationToken cancellationToken);
}
