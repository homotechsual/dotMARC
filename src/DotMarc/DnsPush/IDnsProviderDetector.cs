using DotMarc.Data;

namespace DotMarc.DnsPush;

public interface IDnsProviderDetector
{
    Task<DnsProviderDetectionResult> DetectAsync(string domainName, CancellationToken cancellationToken);
}
