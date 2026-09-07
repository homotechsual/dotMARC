using DotMarc.Data;
using DotMarc.Reporting;
using MudBlazor;
using Xunit;

namespace DotMarc.Tests.Reporting;

public sealed class DnsProviderStatusPresentationTests
{
    [Theory]
    [InlineData(DetectedDnsProvider.Cloudflare, Color.Success, "Cloudflare")]
    [InlineData(DetectedDnsProvider.AzureDns, Color.Success, "Azure DNS")]
    [InlineData(DetectedDnsProvider.Unknown, Color.Warning, "Not recognized")]
    [InlineData(DetectedDnsProvider.NotChecked, Color.Default, "Not checked yet")]
    public void GetColorAndGetLabel_MapEveryProviderToItsExpectedPresentation(DetectedDnsProvider provider, Color expectedColor, string expectedLabel)
    {
        Assert.Equal(expectedColor, DnsProviderStatusPresentation.GetColor(provider));
        Assert.Equal(expectedLabel, DnsProviderStatusPresentation.GetLabel(provider));
    }
}
