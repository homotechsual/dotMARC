using DotMarc.MtaSts;

namespace DotMarc.Tests.Internal;

internal sealed class FakeMtaStsDnsVerifier : IMtaStsDnsVerifier
{
    public MtaStsDnsVerificationResult Result { get; set; } = MtaStsDnsVerificationResult.Resolved;
    public List<string> VerifiedDomains { get; } = [];

    public Task<MtaStsDnsVerificationResult> VerifyAsync(string domainName, string expectedHostingHostname, CancellationToken cancellationToken)
    {
        VerifiedDomains.Add(domainName);
        return Task.FromResult(Result);
    }
}
