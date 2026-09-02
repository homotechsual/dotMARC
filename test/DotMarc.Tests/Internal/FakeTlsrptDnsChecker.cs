using DotMarc.Data;
using DotMarc.Dns;

namespace DotMarc.Tests.Internal;

internal sealed class FakeTlsrptDnsChecker : ITlsrptDnsChecker
{
    public TlsrptCheckResult Result { get; set; } = new(TlsrptCheckStatus.Ok, null);
    public List<string> CheckedDomains { get; } = [];

    public Task<TlsrptCheckResult> CheckAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken)
    {
        CheckedDomains.Add(domainName);
        return Task.FromResult(Result);
    }
}