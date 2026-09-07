using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class SpfDnsCheckerTests
{
    private static (SpfDnsChecker checker, FakeHttpMessageHandler handler) CreateChecker()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new SpfDnsChecker(http), handler);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMissingRecord_WhenNoTxtRecordsExist()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """{"Status":3}""";

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(SpfCheckStatus.MissingRecord, result.Status);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMissingRecord_WhenTxtRecordsExistButNoneStartWithVEqualsSpf1()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"some-other-txt-record\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(SpfCheckStatus.MissingRecord, result.Status);
    }

    [Fact]
    public async Task CheckAsync_ReturnsOk_WhenExactlyOneSpfRecordExists()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=spf1 include:_spf.google.com ~all\""},{"type":16,"data":"\"some-other-txt-record\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(SpfCheckStatus.Ok, result.Status);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task CheckAsync_ReturnsNullSpf_WhenTheRecordIsExactlyVEqualsSpf1DashAll()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=spf1 -all\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(SpfCheckStatus.NullSpf, result.Status);
        Assert.Contains("no senders are authorized", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_ReturnsOk_NotNullSpf_WhenDashAllFollowsARealMechanism()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=spf1 include:_spf.google.com -all\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(SpfCheckStatus.Ok, result.Status);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMisconfigured_WhenARecordLooksLikeSpfButHasTheWrongPrefix()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=spf2 include:_spf.google.com ~all\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(SpfCheckStatus.Misconfigured, result.Status);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMultipleRecords_WhenTwoSpfRecordsExist()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=spf1 include:_spf.google.com ~all\""},{"type":16,"data":"\"v=spf1 include:spf.protection.outlook.com -all\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(SpfCheckStatus.MultipleRecords, result.Status);
    }
}
