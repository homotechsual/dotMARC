// test/DotMarc.Tests/IpEnrichment/RdapIpInfoLookupTests.cs
using System.Net;
using DotMarc.Data;
using DotMarc.IpEnrichment;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.IpEnrichment;

public sealed class RdapIpInfoLookupTests
{
    private static (RdapIpInfoLookup lookup, FakeHttpMessageHandler handler) CreateLookup()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://rdap.org/") };
        return (new RdapIpInfoLookup(http), handler);
    }

    [Fact]
    public async Task LookupAsync_ReturnsOkWithParsedFields_OnASuccessfulResponse()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """
            {"country":"US","entities":[{"roles":["registrant"],"vcardArray":["vcard",[["fn",{},"text","Google LLC"]]]}]}
            """;

        var result = await lookup.LookupAsync("142.250.10.20", CancellationToken.None);

        Assert.Equal(IpLookupStatus.Ok, result.Status);
        Assert.Equal("Google LLC", result.Organization);
        Assert.Equal("US", result.Country);
    }

    [Fact]
    public async Task LookupAsync_RequestsTheExpectedRdapPath()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = "{}";

        await lookup.LookupAsync("142.250.10.20", CancellationToken.None);

        Assert.Equal("https://rdap.org/ip/142.250.10.20", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task LookupAsync_RequestsTheExpectedRdapPath_ForAnIPv6Address()
    {
        // Reproduces the live bug: Uri.EscapeDataString percent-encodes ':' to '%3A', and
        // rdap.org's redirector 400s on that — every IPv6 lookup failed as a result. The path
        // must carry literal colons (legal unescaped in a URL path segment per RFC 3986).
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = "{}";

        await lookup.LookupAsync("2a01:111:f403:c207::3", CancellationToken.None);

        Assert.Equal("https://rdap.org/ip/2a01:111:f403:c207::3", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task LookupAsync_ReturnsLookupFailed_ForAnUnparsableIp_WithoutMakingARequest()
    {
        var (lookup, handler) = CreateLookup();

        var result = await lookup.LookupAsync("not-an-ip", CancellationToken.None);

        Assert.Equal(IpLookupStatus.LookupFailed, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LookupAsync_ReturnsRangeBounds_OnASuccessfulResponse()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """
            {"startAddress":"2a01:110::","endAddress":"2a01:111:ffff:ffff:ffff:ffff:ffff:ffff"}
            """;

        var result = await lookup.LookupAsync("2a01:111:f403:c207::3", CancellationToken.None);

        Assert.Equal("2a01:110::", result.RangeStart);
        Assert.Equal("2a01:111:ffff:ffff:ffff:ffff:ffff:ffff", result.RangeEnd);
    }

    [Fact]
    public async Task LookupAsync_ReturnsNoRangeBounds_WhenTheResponseOmitsThem()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = "{}";

        var result = await lookup.LookupAsync("142.250.10.20", CancellationToken.None);

        Assert.Null(result.RangeStart);
        Assert.Null(result.RangeEnd);
    }

    [Fact]
    public async Task LookupAsync_ReturnsNotFound_On404()
    {
        var (lookup, handler) = CreateLookup();
        handler.StatusCode = HttpStatusCode.NotFound;
        handler.ResponseBody = "{}";

        var result = await lookup.LookupAsync("203.0.113.1", CancellationToken.None);

        Assert.Equal(IpLookupStatus.NotFound, result.Status);
        Assert.Null(result.Organization);
    }

    [Fact]
    public async Task LookupAsync_ReturnsLookupFailed_OnAServerError()
    {
        var (lookup, handler) = CreateLookup();
        handler.StatusCode = HttpStatusCode.InternalServerError;
        handler.ResponseBody = "";

        var result = await lookup.LookupAsync("203.0.113.1", CancellationToken.None);

        Assert.Equal(IpLookupStatus.LookupFailed, result.Status);
    }

    [Fact]
    public async Task LookupAsync_ReturnsLookupFailed_OnA200ResponseWithAnUnparseableBody()
    {
        // Reproduces a WAF interstitial or truncated proxy response: HTTP 200, but the body isn't
        // valid JSON, so RdapResponseParser.Parse's JsonDocument.Parse would throw JsonException
        // if that weren't guarded by the same try/catch as the request itself.
        var (lookup, handler) = CreateLookup();
        handler.StatusCode = HttpStatusCode.OK;
        handler.ResponseBody = "not json";

        var result = await lookup.LookupAsync("203.0.113.1", CancellationToken.None);

        Assert.Equal(IpLookupStatus.LookupFailed, result.Status);
    }

    [Fact]
    public async Task LookupAsync_ReturnsLookupFailed_WhenTheRequestThrows()
    {
        var http = new HttpClient(new ThrowingHttpMessageHandler()) { BaseAddress = new Uri("https://rdap.org/") };
        var lookup = new RdapIpInfoLookup(http);

        var result = await lookup.LookupAsync("203.0.113.1", CancellationToken.None);

        Assert.Equal(IpLookupStatus.LookupFailed, result.Status);
    }

    [Fact]
    public async Task LookupAsync_SetsAUserAgentHeader_OnEveryRequest()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = "{}";

        await lookup.LookupAsync("142.250.10.20", CancellationToken.None);

        Assert.NotEmpty(handler.Requests[0].Headers.UserAgent);
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Simulated network failure.");
    }
}
