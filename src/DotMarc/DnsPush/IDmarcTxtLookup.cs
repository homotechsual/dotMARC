namespace DotMarc.DnsPush;

public interface IDmarcTxtLookup
{
    Task<DnsRecordLookupResult> LookupAsync(string domainName, CancellationToken cancellationToken);
}
