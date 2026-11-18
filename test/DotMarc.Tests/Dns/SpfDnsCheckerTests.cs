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

    private static (SpfDnsChecker Checker, FakeHttpMessageHandler Handler, FakeTxtRecordLookup Includes) CreateCountingChecker(string apexRecord)
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseBody = $$"""{"Status":0,"Answer":[{"type":16,"data":"\"{{apexRecord}}\""}]}"""
        };
        var includes = new FakeTxtRecordLookup();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new SpfDnsChecker(http, includes), handler, includes);
    }

    [Fact]
    public async Task CheckAsync_ReturnsTooManyLookups_PastTen()
    {
        var (checker, _, includes) = CreateCountingChecker("v=spf1 include:big.example a mx ~all");
        includes.TxtByName["big.example"] = ["v=spf1 " + string.Join(' ', Enumerable.Range(1, 9).Select(number => $"exists:{number}.example")) + " ~all"];

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(SpfCheckStatus.TooManyLookups, result.Status);
        Assert.Contains("12 DNS lookups", result.Detail);
        Assert.Contains("include:big.example (10)", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMisconfigured_WhenAnIncludeHasNoSpfRecord()
    {
        var (checker, _, _) = CreateCountingChecker("v=spf1 include:gone.example ~all");

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(SpfCheckStatus.Misconfigured, result.Status);
        Assert.Contains("gone.example", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_StaysOk_WhenCountingLookupsFails()
    {
        var (checker, _, includes) = CreateCountingChecker("v=spf1 include:a.example ~all");
        includes.ThrowOnQuery = new HttpRequestException("DNS is down");

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(SpfCheckStatus.Ok, result.Status);
    }

    [Fact]
    public async Task CheckAsync_ReturnsOk_WithinTheLimit()
    {
        var (checker, _, includes) = CreateCountingChecker("v=spf1 include:_spf.google.com ~all");
        includes.TxtByName["_spf.google.com"] = ["v=spf1 include:_netblocks.google.com ~all"];
        includes.TxtByName["_netblocks.google.com"] = ["v=spf1 ip4:35.190.247.0/24 ~all"];

        Assert.Equal(SpfCheckStatus.Ok, (await checker.CheckAsync("contoso.io", CancellationToken.None)).Status);
    }

    [Fact]
    public async Task CheckAsync_ReturnsTheRangesTheRecordAuthorises()
    {
        var (checker, _, includes) = CreateCountingChecker("v=spf1 include:servers.mcsv.net ip4:192.0.2.0/24 ~all");
        includes.TxtByName["servers.mcsv.net"] = ["v=spf1 ip4:198.51.100.0/24 ~all"];

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(SpfCheckStatus.Ok, result.Status);
        Assert.Equal(
            [new SpfServiceRange(null, "contoso.io", "192.0.2.0/24"), new SpfServiceRange("Mailchimp", "servers.mcsv.net", "198.51.100.0/24")],
            result.Ranges!.OrderBy(range => range.Include));
    }

    [Fact]
    public async Task CheckAsync_ReturnsNoRanges_WhenTheDomainAuthorisesNothing()
    {
        var (missing, missingHandler) = CreateChecker();
        missingHandler.ResponseBody = """{"Status":3}""";
        var (nullSpf, nullHandler) = CreateChecker();
        nullHandler.ResponseBody = """{"Status":0,"Answer":[{"type":16,"data":"\"v=spf1 -all\""}]}""";

        Assert.Empty((await missing.CheckAsync("contoso.io", CancellationToken.None)).Ranges!);
        Assert.Empty((await nullSpf.CheckAsync("contoso.io", CancellationToken.None)).Ranges!);
    }

    [Fact]
    public async Task CheckAsync_ReturnsNullRanges_WhenThereAreSeveralRecords()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=spf1 ip4:192.0.2.0/24 ~all\""},{"type":16,"data":"\"v=spf1 -all\""}]}
            """;

        Assert.Null((await checker.CheckAsync("contoso.io", CancellationToken.None)).Ranges);
    }

    [Fact]
    public async Task CheckAsync_ReturnsNullRanges_WhenExpansionFails()
    {
        var (checker, _, includes) = CreateCountingChecker("v=spf1 include:a.example ~all");
        includes.ThrowOnQuery = new HttpRequestException("DNS is down");

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(SpfCheckStatus.Ok, result.Status);
        Assert.Null(result.Ranges);
    }
}
