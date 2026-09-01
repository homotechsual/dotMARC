namespace DotMarc.DnsPush;

public interface IDmarcTxtLookup
{
    Task<string?> LookupAsync(string domainName, CancellationToken cancellationToken);
}
