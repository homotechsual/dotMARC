namespace DotMarc.Dns;

public interface ITlsrptDnsChecker
{
    Task<TlsrptCheckResult> CheckAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken);
}