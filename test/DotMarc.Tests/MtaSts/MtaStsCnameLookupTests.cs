using DotMarc.MtaSts;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.MtaSts;

public sealed class MtaStsCnameLookupTests
{
    private static (MtaStsCnameLookup lookup, FakeHttpMessageHandler handler) CreateLookup()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new MtaStsCnameLookup(http), handler);
    }

    [Fact]
    public async Task LookupAsync_ReturnsNull_WhenNoCnameRecordExists()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """{"Status":3}""";

        var result = await lookup.LookupAsync("contoso.io", CancellationToken.None);

        Assert.Null(result);
        Assert.Contains("mta-sts.contoso.io", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("type=CNAME", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task LookupAsync_ReturnsTrimmedCnameTarget()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":5,"data":"dotmarc-abc123.azurecontainerapps.io."}]}
            """;

        var result = await lookup.LookupAsync("contoso.io", CancellationToken.None);

        Assert.Equal("dotmarc-abc123.azurecontainerapps.io", result);
    }

    [Fact]
    public async Task LookupAsuidTxtAsync_ReturnsNull_WhenNoRecordExists()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """{"Status":3}""";

        var result = await lookup.LookupAsuidTxtAsync("contoso.io", CancellationToken.None);

        Assert.Null(result);
        Assert.Contains("asuid.mta-sts.contoso.io", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("type=TXT", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task LookupAsuidTxtAsync_ReturnsTheUnquotedValue()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"E9FBBD208F19367668F9FF7CC15561B\""}]}
            """;

        var result = await lookup.LookupAsuidTxtAsync("contoso.io", CancellationToken.None);

        Assert.Equal("E9FBBD208F19367668F9FF7CC15561B", result);
    }
}
