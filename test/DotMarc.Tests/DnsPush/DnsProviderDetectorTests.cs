using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DnsProviderDetectorTests
{
    private static (DnsProviderDetector detector, FakeHttpMessageHandler handler) CreateDetector()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new DnsProviderDetector(http), handler);
    }

    [Fact]
    public async Task DetectAsync_ReturnsCloudflare_WhenNsRecordsAreCloudflares()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":2,"data":"ana.ns.cloudflare.com."},{"type":2,"data":"bob.ns.cloudflare.com."}]}
            """;

        var result = await detector.DetectAsync("contoso.io", CancellationToken.None);

        Assert.Equal(DetectedDnsProvider.Cloudflare, result);
    }

    [Theory]
    [InlineData("ns1-01.azure-dns.com.")]
    [InlineData("ns2-01.azure-dns.net.")]
    [InlineData("ns3-01.azure-dns.org.")]
    [InlineData("ns4-01.azure-dns.info.")]
    public async Task DetectAsync_ReturnsAzureDns_ForEachAzureDnsSuffix(string nsHost)
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBody = $$"""
            {"Status":0,"Answer":[{"type":2,"data":"{{nsHost}}"}]}
            """;

        var result = await detector.DetectAsync("contoso.io", CancellationToken.None);

        Assert.Equal(DetectedDnsProvider.AzureDns, result);
    }

    [Fact]
    public async Task DetectAsync_ReturnsUnknown_ForAnUnrecognizedProvider()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":2,"data":"dns1.registrar-nameservers.com."}]}
            """;

        var result = await detector.DetectAsync("contoso.io", CancellationToken.None);

        Assert.Equal(DetectedDnsProvider.Unknown, result);
    }

    [Fact]
    public async Task DetectAsync_ReturnsUnknown_WhenNoNsRecordsExist()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBody = """{"Status":3}""";

        var result = await detector.DetectAsync("contoso.io", CancellationToken.None);

        Assert.Equal(DetectedDnsProvider.Unknown, result);
    }

    [Fact]
    public async Task DetectAsync_QueriesNsRecordType_ForTheGivenDomain()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBody = """{"Status":3}""";

        await detector.DetectAsync("contoso.io", CancellationToken.None);

        Assert.Contains("contoso.io", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("type=NS", handler.Requests[0].RequestUri!.ToString());
    }
}
