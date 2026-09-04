namespace DotMarc.MtaSts;

public interface IMtaStsCnameLookup
{
    Task<string?> LookupAsync(string domainName, CancellationToken cancellationToken);

    /// <summary>Fetches the raw, currently-live asuid.mta-sts.&lt;domain&gt; TXT record value (the
    /// Azure Container Apps domain-ownership verification record) — used the same way LookupAsync
    /// is, to decide Create vs. Merge before pushing.</summary>
    Task<string?> LookupAsuidTxtAsync(string domainName, CancellationToken cancellationToken);
}
