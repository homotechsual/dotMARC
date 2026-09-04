namespace DotMarc.DnsPush;

public interface ITlsrptTxtLookup
{
    Task<DnsRecordLookupResult> LookupAsync(string domainName, CancellationToken cancellationToken);
}
