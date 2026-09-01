using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DmarcTxtLookupTests
{
    private static (DmarcTxtLookup lookup, FakeHttpMessageHandler handler) CreateLookup()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new DmarcTxtLookup(http), handler);
    }

    [Fact]
    public async Task LookupAsync_ReturnsNull_WhenNoRecordExists()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """{"Status":3}""";

        var result = await lookup.LookupAsync("contoso.io", CancellationToken.None);

        Assert.Null(result);
        Assert.Contains("_dmarc.contoso.io", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task LookupAsync_ReturnsTheUnquotedValue()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk\""}]}
            """;

        var result = await lookup.LookupAsync("contoso.io", CancellationToken.None);

        Assert.Equal("v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk", result);
    }

    [Fact]
    public async Task LookupAsync_JoinsMultiSegmentValues()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; \" \"rua=mailto:rua.dmarc@mjco.uk\""}]}
            """;

        var result = await lookup.LookupAsync("contoso.io", CancellationToken.None);

        Assert.Equal("v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk", result);
    }
}
