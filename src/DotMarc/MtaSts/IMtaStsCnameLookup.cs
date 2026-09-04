namespace DotMarc.MtaSts;

public interface IMtaStsCnameLookup
{
    Task<string?> LookupAsync(string domainName, CancellationToken cancellationToken);
}
