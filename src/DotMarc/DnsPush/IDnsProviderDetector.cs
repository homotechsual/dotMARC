namespace DotMarc.DnsPush;

public interface IDnsProviderDetector
{
    Task<DetectedDnsProvider> DetectAsync(string domainName, CancellationToken cancellationToken);
}
