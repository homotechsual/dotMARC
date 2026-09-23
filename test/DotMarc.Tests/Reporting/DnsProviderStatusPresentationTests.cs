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
    [InlineData(DetectedDnsProvider.GoogleCloudDns, Color.Success, "Google Cloud DNS")]
    [InlineData(DetectedDnsProvider.Microsoft365, Color.Info, "Microsoft 365")]
    [InlineData(DetectedDnsProvider.AmazonRoute53, Color.Info, "Amazon Route 53")]
    [InlineData(DetectedDnsProvider.GoDaddy, Color.Info, "GoDaddy")]
    [InlineData(DetectedDnsProvider.Namecheap, Color.Info, "Namecheap")]
    [InlineData(DetectedDnsProvider.DigitalOcean, Color.Info, "DigitalOcean")]
    [InlineData(DetectedDnsProvider.Ovh, Color.Info, "OVH")]
    [InlineData(DetectedDnsProvider.Gandi, Color.Info, "Gandi")]
    [InlineData(DetectedDnsProvider.Ns1, Color.Info, "NS1")]
    [InlineData(DetectedDnsProvider.DnsMadeEasy, Color.Info, "DNS Made Easy")]
    [InlineData(DetectedDnsProvider.Vercel, Color.Info, "Vercel")]
    [InlineData(DetectedDnsProvider.Unknown, Color.Warning, "Not recognised")]
    [InlineData(DetectedDnsProvider.NotChecked, Color.Default, "Not checked yet")]
    public void GetColorAndGetLabel_MapEveryProviderToItsExpectedPresentation(DetectedDnsProvider provider, Color expectedColor, string expectedLabel)
    {
        Assert.Equal(expectedColor, DnsProviderStatusPresentation.GetColor(provider));
        Assert.Equal(expectedLabel, DnsProviderStatusPresentation.GetLabel(provider));
    }
}
