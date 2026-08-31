using DotMarc.MtaSts;

namespace DotMarc.Tests.Internal;

internal sealed class FakeMtaStsServingVerifier : IMtaStsServingVerifier
{
    public bool IsServing { get; set; } = true;
    public List<string> CheckedDomains { get; } = [];

    public Task<bool> IsServingCorrectlyAsync(string domainName, string expectedPolicyText, CancellationToken cancellationToken)
    {
        CheckedDomains.Add(domainName);
        return Task.FromResult(IsServing);
    }
}
