using DotMarc.Data;
using DotMarc.Dns;

namespace DotMarc.Tests.Internal;

internal sealed class FakeDmarcDnsChecker : IDmarcDnsChecker
{
    public DmarcCheckResult Result { get; set; } = new(DmarcCheckStatus.Ok, null);
    public bool ShouldThrow { get; set; }
    public List<string> CheckedDomains { get; } = [];

    public DmarcAuthorizationCheckResult AuthorizationResult { get; set; } = new(DmarcAuthorizationCheckStatus.NotApplicable, null);
    public bool AuthorizationShouldThrow { get; set; }
    public List<string> AuthorizationCheckedDomains { get; } = [];

    public Task<DmarcCheckResult> CheckAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken)
    {
        CheckedDomains.Add(domainName);
        if (ShouldThrow)
        {
            throw new HttpRequestException("Simulated Cloudflare failure.");
        }
        return Task.FromResult(Result);
    }

    public Task<DmarcAuthorizationCheckResult> CheckAuthorizationAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken)
    {
        AuthorizationCheckedDomains.Add(domainName);
        if (AuthorizationShouldThrow)
        {
            throw new HttpRequestException("Simulated Cloudflare failure.");
        }
        return Task.FromResult(AuthorizationResult);
    }
}
