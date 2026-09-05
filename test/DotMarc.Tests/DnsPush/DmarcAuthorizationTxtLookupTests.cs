using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DmarcAuthorizationTxtLookupTests
{
    private static (DmarcAuthorizationTxtLookup lookup, FakeHttpMessageHandler handler) CreateLookup()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new DmarcAuthorizationTxtLookup(http), handler);
    }

    [Fact]
    public async Task LookupAsync_ReturnsNull_WhenNoRecordExists()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """{"Status":3}""";

        var result = await lookup.LookupAsync("contoso.io._report._dmarc.mjco.uk", CancellationToken.None);

        Assert.Null(result.DirectValue);
        Assert.Null(result.DelegatedToCname);
        Assert.Contains("contoso.io._report._dmarc.mjco.uk", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task LookupAsync_ReturnsTheUnquotedValue()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1;\""}]}
            """;

        var result = await lookup.LookupAsync("contoso.io._report._dmarc.mjco.uk", CancellationToken.None);

        Assert.Equal("v=DMARC1;", result.DirectValue);
    }

    [Fact]
    public async Task LookupAsync_DetectsCnameDelegation()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":5,"data":"delegated.example.com."}]}
            """;

        var result = await lookup.LookupAsync("contoso.io._report._dmarc.mjco.uk", CancellationToken.None);

        Assert.Null(result.DirectValue);
        Assert.Equal("delegated.example.com.", result.DelegatedToCname);
    }
}
