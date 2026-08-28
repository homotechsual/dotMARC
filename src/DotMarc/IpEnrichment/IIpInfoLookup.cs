// src/DotMarc/IpEnrichment/IIpInfoLookup.cs
namespace DotMarc.IpEnrichment;

public interface IIpInfoLookup
{
    Task<IpLookupResult> LookupAsync(string ip, CancellationToken cancellationToken);
}
