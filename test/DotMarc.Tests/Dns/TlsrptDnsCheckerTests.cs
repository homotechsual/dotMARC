using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class TlsrptDnsCheckerTests
{
    private static (TlsrptDnsChecker checker, FakeHttpMessageHandler handler) CreateChecker()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new TlsrptDnsChecker(http), handler);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMissingOwnRecord_WhenNoTlsrptRecordExists()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """{"Status":3}""";

        var result = await checker.CheckAsync("contoso.io", "tlsrpt@reports.example", CancellationToken.None);

        Assert.Equal(TlsrptCheckStatus.MissingOwnRecord, result.Status);
        Assert.Contains("_smtp._tls.contoso.io", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task CheckAsync_ReturnsMisconfigured_WhenRuaDoesNotMatchTheTlsrptMailbox()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=TLSRPTv1; rua=mailto:other@example.com\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "tlsrpt@reports.example", CancellationToken.None);

        Assert.Equal(TlsrptCheckStatus.Misconfigured, result.Status);
        Assert.Contains("other@example.com", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_ReturnsOk_WhenTlsrptRuaMatchesTheConfiguredMailbox()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=TLSRPTv1; rua=mailto:other@example.com,mailto:tlsrpt@reports.example\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "tlsrpt@reports.example", CancellationToken.None);

        Assert.Equal(TlsrptCheckStatus.Ok, result.Status);
    }
}