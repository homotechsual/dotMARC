using DotMarc.Data;
using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DnsPushSelectionTests
{
    [Fact]
    public void GroupsByConfiguredProviderAndCountsEachIdentityOnce()
    {
        var domain = new SelectedDnsDomain(1, "one.example", DetectedDnsProvider.Cloudflare);
        var result = DnsPushSelection.Group([domain, domain, new(2, "two.example", DetectedDnsProvider.AzureDns),
            new(3, "three.example", DetectedDnsProvider.GoDaddy), new(4, "four.example", DetectedDnsProvider.Unknown),
            new(5, "five.example", DetectedDnsProvider.GoogleCloudDns)], ["cloudflare", "azure-dns"]);
        Assert.Equal(["azure-dns", "cloudflare"], result.Providers.Select(group => group.Provider));
        Assert.All(result.Providers, group => Assert.Single(group.Domains));
        Assert.Equal([5, 4, 3], result.Manual.Select(domain => domain.Id));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(50, false)]
    [InlineData(51, true)]
    public void ProviderLimitIsMeasuredExactly(int count, bool overLimit)
    {
        var result = DnsPushSelection.Group(Enumerable.Range(1, count).Select(id => new SelectedDnsDomain(id, $"domain-{id}.example", DetectedDnsProvider.Cloudflare)), ["cloudflare"]);
        if (count == 0) Assert.Empty(result.Providers);
        else Assert.Equal(overLimit, Assert.Single(result.Providers).OverLimit);
        Assert.Empty(result.Manual);
    }

    [Fact]
    public void EveryStatusAndRefusalHasActionableText()
    {
        foreach (var status in Enum.GetValues<DnsPushBatchDomainStatus>())
        {
            var row = new DnsPushBatchDomain { Status = status };
            Assert.False(string.IsNullOrWhiteSpace(DnsPushBatchPresentation.Label(row)));
            Assert.False(string.IsNullOrWhiteSpace(DnsPushBatchPresentation.Explain(row)));
        }
        Assert.Contains("override", DnsPushBatchPresentation.Explain(new() { Detail = "report-address-conflict" }));
    }
}
