using DotMarc.Data;
using DotMarc.Dns;

namespace DotMarc.Tests.Internal;

internal sealed class FakeMxDnsChecker : IMxDnsChecker
{
    public MxCheckResult Result { get; set; } = new(MxCheckStatus.Ok, null);
    public bool ShouldThrow { get; set; }
    public List<string> CheckedDomains { get; } = [];

    public Task<MxCheckResult> CheckAsync(string domainName, CancellationToken cancellationToken)
    {
        CheckedDomains.Add(domainName);
        if (ShouldThrow)
        {
            throw new HttpRequestException("Simulated Cloudflare failure.");
        }
        return Task.FromResult(Result);
    }
}
