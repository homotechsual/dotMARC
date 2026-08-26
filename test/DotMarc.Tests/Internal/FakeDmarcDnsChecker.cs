using DotMarc.Data;
using DotMarc.Dns;

namespace DotMarc.Tests.Internal;

internal sealed class FakeDmarcDnsChecker : IDmarcDnsChecker
{
    public DmarcCheckResult Result { get; set; } = new(DmarcCheckStatus.Ok, null);
    public bool ShouldThrow { get; set; }
    public List<string> CheckedDomains { get; } = [];

    public Task<DmarcCheckResult> CheckAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken)
    {
        CheckedDomains.Add(domainName);
        if (ShouldThrow)
        {
            throw new HttpRequestException("Simulated Cloudflare failure.");
        }
        return Task.FromResult(Result);
    }
}
