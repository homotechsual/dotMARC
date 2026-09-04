using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class TlsrptTxtLookupTests
{
    private static (TlsrptTxtLookup lookup, FakeHttpMessageHandler handler) CreateLookup()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new TlsrptTxtLookup(http), handler);
    }

    [Fact]
    public async Task LookupAsync_ReturnsNull_WhenNoRecordExists()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """{"Status":3}""";

        var result = await lookup.LookupAsync("contoso.io", CancellationToken.None);

        Assert.Null(result.DirectValue);
        Assert.Null(result.DelegatedToCname);
        Assert.Contains("_smtp._tls.contoso.io", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task LookupAsync_ReturnsTheUnquotedValue()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=TLSRPTv1; rua=mailto:tls-reports@mjco.uk\""}]}
            """;

        var result = await lookup.LookupAsync("contoso.io", CancellationToken.None);

        Assert.Equal("v=TLSRPTv1; rua=mailto:tls-reports@mjco.uk", result.DirectValue);
    }

    [Fact]
    public async Task LookupAsync_DetectsCnameDelegation()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":5,"data":"contoso_io__smtp_tls.example-tls-provider.net."},{"type":16,"data":"\"v=TLSRPTv1; rua=mailto:reports@example-tls-provider.net\""}]}
            """;

        var result = await lookup.LookupAsync("contoso.io", CancellationToken.None);

        Assert.Equal("v=TLSRPTv1; rua=mailto:reports@example-tls-provider.net", result.DirectValue);
        Assert.Equal("contoso_io__smtp_tls.example-tls-provider.net.", result.DelegatedToCname);
    }
}
