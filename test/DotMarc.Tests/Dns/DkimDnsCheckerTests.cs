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

    private static string Answers(params (int Type, string Data)[] answers) =>
        System.Text.Json.JsonSerializer.Serialize(new { Status = 0, Answer = answers.Select(answer => new { type = answer.Type, data = answer.Data }) });

    private static readonly DkimExpectedRecord ExpectedCname =
        new("selector1", DkimRecordType.Cname, "selector1-contoso-io._domainkey.contoso.onmicrosoft.com");

    [Fact]
    public async Task CheckAsync_CnameExpected_IsMissing_WhenNothingIsThere()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """{"Status":3}""";

        var result = await checker.CheckAsync("contoso.io", ["selector1"], CancellationToken.None, [ExpectedCname]);

        Assert.Equal(DkimCheckStatus.Missing, result.Status);
    }

    [Fact]
    public async Task CheckAsync_CnameExpected_IsMisconfigured_WhenItPointsElsewhere()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = Answers((5, "old-target.example."), (16, "\"v=DKIM1; p=ABC\""));

        var result = await checker.CheckAsync("contoso.io", ["selector1"], CancellationToken.None, [ExpectedCname]);

        Assert.Equal(DkimCheckStatus.Misconfigured, result.Status);
        Assert.Contains("selector1 points to old-target.example, expected selector1-contoso-io._domainkey.contoso.onmicrosoft.com", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_CnameExpected_IsMissing_WithAHint_WhenTheKeyIsNotPublishedYet()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = Answers((5, "Selector1-contoso-io._domainkey.contoso.onmicrosoft.com."));

        var result = await checker.CheckAsync("contoso.io", ["selector1"], CancellationToken.None, [ExpectedCname]);

        Assert.Equal(DkimCheckStatus.Missing, result.Status);
        Assert.Contains("turn on DKIM signing", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_CnameExpected_IsOk_WhenItMatchesAndTheKeyIsThere()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = Answers((5, "selector1-contoso-io._domainkey.contoso.onmicrosoft.com."), (16, "\"v=DKIM1; k=rsa; p=MIIB\""));

        Assert.Equal(DkimCheckStatus.Ok, (await checker.CheckAsync("contoso.io", ["selector1"], CancellationToken.None, [ExpectedCname])).Status);
    }

    [Fact]
    public async Task CheckAsync_CnameExpected_IsMisconfigured_WhenAPlainTxtRecordIsThere()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = Answers((16, "\"v=DKIM1; p=ABC\""));

        var result = await checker.CheckAsync("contoso.io", ["selector1"], CancellationToken.None, [ExpectedCname]);

        Assert.Equal(DkimCheckStatus.Misconfigured, result.Status);
        Assert.Contains("a CNAME", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_TxtExpected_IsMisconfigured_WhenTheKeyDiffers()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = Answers((16, "\"v=DKIM1; k=rsa; p=OLDKEY\""));

        var result = await checker.CheckAsync("contoso.io", ["google"], CancellationToken.None, [new DkimExpectedRecord("google", DkimRecordType.Txt, "v=DKIM1; k=rsa; p=NEWKEY")]);

        Assert.Equal(DkimCheckStatus.Misconfigured, result.Status);
        Assert.Contains("google", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_TxtExpected_MatchesDespiteWhitespace()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = Answers((16, "\"v=DKIM1; k=rsa; \" \"p=MIIB IjAN\""));

        var result = await checker.CheckAsync("contoso.io", ["google"], CancellationToken.None, [new DkimExpectedRecord("google", DkimRecordType.Txt, "v=DKIM1; k=rsa; p=MIIBIjAN")]);

        Assert.Equal(DkimCheckStatus.Ok, result.Status);
    }

    [Fact]
    public async Task LookupSelectorAsync_ReportsAPublishedCname()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":5,"data":"selector1-contoso-io._domainkey.contoso.n-v1.dkim.mail.microsoft."}]}
            """;

        var published = await checker.LookupSelectorAsync("contoso.io", "selector1", CancellationToken.None);

        Assert.Equal("selector1-contoso-io._domainkey.contoso.n-v1.dkim.mail.microsoft.", published.DelegatedToCname);
        Assert.Contains("name=selector1._domainkey.contoso.io", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task LookupSelectorAsync_ReportsAPublishedTxtKey()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DKIM1; k=rsa; p=MIIBIjANBgkq\""}]}
            """;

        var published = await checker.LookupSelectorAsync("contoso.io", "google", CancellationToken.None);

        Assert.Equal("v=DKIM1; k=rsa; p=MIIBIjANBgkq", published.DirectValue);
        Assert.Null(published.DelegatedToCname);
    }
}
