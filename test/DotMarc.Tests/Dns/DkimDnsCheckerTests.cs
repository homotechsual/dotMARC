using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class DkimDnsCheckerTests
{
    private static (DkimDnsChecker checker, FakeHttpMessageHandler handler) CreateChecker()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new DkimDnsChecker(http), handler);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMissing_WhenTheSelectorHasNoTxtRecord()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """{"Status":3}""";

        var result = await checker.CheckAsync("contoso.io", ["selector1"], CancellationToken.None);

        Assert.Equal(DkimCheckStatus.Missing, result.Status);
        Assert.Contains("selector1", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMisconfigured_WhenTheRecordHasNoPTag()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DKIM1; k=rsa\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", ["selector1"], CancellationToken.None);

        Assert.Equal(DkimCheckStatus.Misconfigured, result.Status);
        Assert.Contains("selector1", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_ReturnsOk_WhenTheRecordHasAPTag()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DKIM1; k=rsa; p=MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQC7\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", ["selector1"], CancellationToken.None);

        Assert.Equal(DkimCheckStatus.Ok, result.Status);
        Assert.Null(result.Detail);
        Assert.Contains("selector1._domainkey.contoso.io", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task CheckAsync_ReturnsMissing_WhenOneOfTwoSelectorsHasNoRecord()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBodies.Enqueue("""
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DKIM1; k=rsa; p=MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQC7\""}]}
            """);
        handler.ResponseBodies.Enqueue("""{"Status":3}""");

        var result = await checker.CheckAsync("contoso.io", ["selector1", "selector2"], CancellationToken.None);

        Assert.Equal(DkimCheckStatus.Missing, result.Status);
        Assert.Contains("selector2", result.Detail);
        Assert.DoesNotContain("selector1", result.Detail);
        Assert.Equal(2, handler.Requests.Count);
    }
}
