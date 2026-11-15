using DotMarc.MtaSts;

namespace DotMarc.Tests.Internal;

internal sealed class FakeMtaStsDnsVerifier : IMtaStsDnsVerifier
{
    public MtaStsDnsVerificationResult Result { get; set; } = MtaStsDnsVerificationResult.Resolved;
    public string? CurrentTarget { get; set; }
    public bool ShouldThrowOnVerify { get; set; }
    public List<string> VerifiedDomains { get; } = [];

    public Task<MtaStsDnsVerification> VerifyAsync(string domainName, string expectedHostingHostname, CancellationToken cancellationToken)
    {
        VerifiedDomains.Add(domainName);
        if (ShouldThrowOnVerify)
        {
            throw new HttpRequestException("DNS lookup failed.");
        }

        return Task.FromResult(new MtaStsDnsVerification(Result, CurrentTarget));
    }
}
