namespace DotMarc.DnsPush;

public interface ITlsrptTxtLookup
{
    Task<string?> LookupAsync(string domainName, CancellationToken cancellationToken);
}