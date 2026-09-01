using DotMarc.MtaSts;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.MtaSts;

public sealed class MxHostsLookupTests
{
    private static (MxHostsLookup lookup, FakeHttpMessageHandler handler) CreateLookup()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new MxHostsLookup(http), handler);
    }

    [Fact]
    public async Task LookupAsync_ReturnsEmpty_WhenNoMxRecordExists()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """{"Status":3}""";

        var result = await lookup.LookupAsync("contoso.io", CancellationToken.None);

        Assert.Empty(result);
        Assert.Contains("contoso.io", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("type=MX", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task LookupAsync_ExtractsExchangeHostname_StrippingPreferenceAndTrailingDot()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":15,"data":"10 mail.contoso.io."}]}
            """;

        var result = await lookup.LookupAsync("contoso.io", CancellationToken.None);

        Assert.Equal(["mail.contoso.io"], result);
    }

    [Fact]
    public async Task LookupAsync_OrdersByPreference_LowestFirst()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":15,"data":"20 backup-mail.contoso.io."},{"type":15,"data":"10 mail.contoso.io."}]}
            """;

        var result = await lookup.LookupAsync("contoso.io", CancellationToken.None);

        Assert.Equal(["mail.contoso.io", "backup-mail.contoso.io"], result);
    }

    [Fact]
    public async Task LookupAsync_DeduplicatesRepeatedExchangeHostnames()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":15,"data":"10 mail.contoso.io."},{"type":15,"data":"20 mail.contoso.io."}]}
            """;

        var result = await lookup.LookupAsync("contoso.io", CancellationToken.None);

        Assert.Equal(["mail.contoso.io"], result);
    }

    [Fact]
    public async Task LookupAsync_IgnoresNonMxAnswers()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"unrelated txt record\""},{"type":15,"data":"10 mail.contoso.io."}]}
            """;

        var result = await lookup.LookupAsync("contoso.io", CancellationToken.None);

        Assert.Equal(["mail.contoso.io"], result);
    }
}
