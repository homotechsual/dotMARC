namespace DotMarc.MtaSts;

public interface IMxHostsLookup
{
    Task<List<string>> LookupAsync(string domainName, CancellationToken cancellationToken);
}
