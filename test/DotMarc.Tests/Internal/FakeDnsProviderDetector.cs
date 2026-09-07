using DotMarc.Data;
using DotMarc.DnsPush;

namespace DotMarc.Tests.Internal;

internal sealed class FakeDnsProviderDetector : IDnsProviderDetector
{
    public DnsProviderDetectionResult Result { get; set; } = new(DetectedDnsProvider.Unknown, "");
    public bool ShouldThrow { get; set; }
    public List<string> CheckedDomains { get; } = [];

    public Task<DnsProviderDetectionResult> DetectAsync(string domainName, CancellationToken cancellationToken)
    {
        CheckedDomains.Add(domainName);
        if (ShouldThrow)
        {
            throw new HttpRequestException("Simulated Cloudflare failure.");
        }
        return Task.FromResult(Result);
    }
}
