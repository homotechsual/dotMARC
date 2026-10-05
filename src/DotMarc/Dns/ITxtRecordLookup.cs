using DotMarc.DnsPush;

namespace DotMarc.Dns;

/// <summary>TXT lookups over the same DNS-over-HTTPS resolver the checks use, for the SPF lookup counter, the SPF
/// editor and the SPF and DKIM pushes.</summary>
public interface ITxtRecordLookup
{
    /// <summary>Every TXT value at the name, each with its quoted strings joined. Empty when there are none.</summary>
    Task<IReadOnlyList<string>> GetTxtValuesAsync(string name, CancellationToken cancellationToken);

    /// <summary>The TXT value at the name, noting when the name is a CNAME to somewhere else.</summary>
    Task<DnsRecordLookupResult> LookupWithCnameAsync(string name, CancellationToken cancellationToken);
}
