namespace DotMarc.Dns;

public interface IDmarcDnsChecker
{
    Task<DmarcCheckResult> CheckAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken);
}
