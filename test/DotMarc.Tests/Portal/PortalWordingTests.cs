using DotMarc.Data;
using DotMarc.Portal;
using Xunit;

namespace DotMarc.Tests.Portal;

public sealed class PortalWordingTests
{
    [Theory]
    [InlineData(null, null, null, "No DMARC policy was found, so receivers decide for themselves what to do with mail that fails.")]
    [InlineData(DmarcPolicyLevel.Reject, null, null, "Mail that fails DMARC is rejected.")]
    [InlineData(DmarcPolicyLevel.Quarantine, 50, DmarcPolicyLevel.Reject, "Mail that fails DMARC is sent to spam (50% of it). Subdomains: rejected.")]
    public void ThePolicy_IsSaidInPlainWords(DmarcPolicyLevel? policy, int? percent, DmarcPolicyLevel? subdomainPolicy, string expected)
    {
        var domain = new Domain { Name = "aurora-retail.example", DmarcPolicy = policy, DmarcPercent = percent, DmarcSubdomainPolicy = subdomainPolicy };

        Assert.Equal(expected, PortalWording.PolicySentence(domain));
    }

    [Fact]
    public void MtaSts_IsOnlyListedWhenSetUp()
    {
        var domain = new Domain { Name = "aurora-retail.example", MtaStsStatus = MtaStsStatus.NotConfigured };

        Assert.DoesNotContain(PortalWording.HealthRows(domain), row => row.Name == "MTA-STS");
    }
}
