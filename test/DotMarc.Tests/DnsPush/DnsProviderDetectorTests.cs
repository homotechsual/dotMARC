using DotMarc.Data;
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

        Assert.Equal(DetectedDnsProvider.Cloudflare, result.Provider);
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

        Assert.Equal(DetectedDnsProvider.AzureDns, result.Provider);
    }

    [Fact]
    public async Task DetectAsync_ReturnsGoogleCloudDns_ForTheGoogleDomainsNsSuffix()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":2,"data":"ns-cloud-a1.googledomains.com."},{"type":2,"data":"ns-cloud-a2.googledomains.com."}]}
            """;

        var result = await detector.DetectAsync("contoso.io", CancellationToken.None);

        Assert.Equal(DetectedDnsProvider.GoogleCloudDns, result.Provider);
        Assert.Equal("contoso.io", result.ZoneName);
    }

    [Fact]
    public async Task DetectAsync_ReturnsUnknown_ForAnUnrecognizedProvider()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":2,"data":"dns1.registrar-nameservers.com."}]}
            """;

        var result = await detector.DetectAsync("contoso.io", CancellationToken.None);

        Assert.Equal(DetectedDnsProvider.Unknown, result.Provider);
    }

    [Fact]
    public async Task DetectAsync_ReturnsUnknown_WhenNoNsRecordsExist()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBody = """{"Status":3}""";

        var result = await detector.DetectAsync("contoso.io", CancellationToken.None);

        Assert.Equal(DetectedDnsProvider.Unknown, result.Provider);
    }

    [Fact]
    public async Task DetectAsync_WalksUpToTheParentZone_WhenTheSubdomainHasNoNsRecordsOfItsOwn()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBodies.Enqueue("""{"Status":0}"""); // services.wrc.wales: no Answer at all
        handler.ResponseBodies.Enqueue("""
            {"Status":0,"Answer":[{"type":2,"data":"ns1-03.azure-dns.com."},{"type":2,"data":"ns2-03.azure-dns.net."}]}
            """); // wrc.wales: Azure DNS

        var result = await detector.DetectAsync("services.wrc.wales", CancellationToken.None);

        Assert.Equal(DetectedDnsProvider.AzureDns, result.Provider);
        Assert.Equal("wrc.wales", result.ZoneName);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("services.wrc.wales", handler.Requests[0].RequestUri!.ToString());
        Assert.DoesNotContain("services.wrc.wales", handler.Requests[1].RequestUri!.ToString());
        Assert.Contains("name=wrc.wales", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task DetectAsync_WalksUpTwoLevels_WhenNeitherTheHostnameNorItsImmediateParentHasNsRecords()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBodies.Enqueue("""{"Status":0}"""); // a.b.contoso.io: no Answer
        handler.ResponseBodies.Enqueue("""{"Status":0}"""); // b.contoso.io: no Answer
        handler.ResponseBodies.Enqueue("""
            {"Status":0,"Answer":[{"type":2,"data":"ana.ns.cloudflare.com."}]}
            """); // contoso.io: Cloudflare

        var result = await detector.DetectAsync("a.b.contoso.io", CancellationToken.None);

        Assert.Equal(DetectedDnsProvider.Cloudflare, result.Provider);
        Assert.Equal("contoso.io", result.ZoneName);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task DetectAsync_ReturnsUnknownAndTheOriginalHostname_WhenNoAncestorWithinTheCapHasNsRecords()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBody = """{"Status":0}"""; // every query in the walk: no Answer

        var result = await detector.DetectAsync("f.e.d.c.b.a.contoso.io", CancellationToken.None);

        Assert.Equal(DetectedDnsProvider.Unknown, result.Provider);
        Assert.Equal("f.e.d.c.b.a.contoso.io", result.ZoneName);
        Assert.Equal(6, handler.Requests.Count);
    }

    [Fact]
    public async Task DetectAsync_StopsAtTheFirstAncestorWithNsRecords_EvenWhenTheProviderIsUnrecognized()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBodies.Enqueue("""{"Status":0}"""); // sub.contoso.io: no Answer
        handler.ResponseBodies.Enqueue("""
            {"Status":0,"Answer":[{"type":2,"data":"dns1.registrar-nameservers.com."}]}
            """); // contoso.io: NS records exist but match no known provider

        var result = await detector.DetectAsync("sub.contoso.io", CancellationToken.None);

        Assert.Equal(DetectedDnsProvider.Unknown, result.Provider);
        Assert.Equal("contoso.io", result.ZoneName);
        Assert.Equal(2, handler.Requests.Count);
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

    [Fact]
    public async Task DetectAsync_PropagatesHttpRequestException_MatchingSiblingCheckersConvention()
    {
        var (detector, handler) = CreateDetector();
        handler.StatusCode = System.Net.HttpStatusCode.InternalServerError;

        await Assert.ThrowsAsync<HttpRequestException>(() => detector.DetectAsync("contoso.io", CancellationToken.None));
    }
}
