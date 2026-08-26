using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Dns;

public class DmarcDnsCheckerTests
{
    private static (DmarcDnsChecker checker, FakeHttpMessageHandler handler) CreateChecker()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new DmarcDnsChecker(http), handler);
    }

    private const string NxDomainResponse = """{"Status":3}""";

    [Fact]
    public async Task CheckAsync_ReturnsMissingOwnRecord_WhenNoDmarcRecordExists()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = NxDomainResponse;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.MissingOwnRecord, result.Status);
        Assert.Contains("_dmarc.contoso.io", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task CheckAsync_ReturnsMisconfigured_WhenRecordDoesNotStartWithVDmarc1()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"not a dmarc record\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Misconfigured, result.Status);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMisconfigured_WhenRuaDoesNotMatchTheConfiguredMailbox()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:other@example.com\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Misconfigured, result.Status);
        Assert.Contains("other@example.com", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_ReturnsOk_WithNoSecondQuery_WhenMailboxDomainMatchesTheMonitoredDomain()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:dmarc@contoso.io\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "dmarc@contoso.io", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Ok, result.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CheckAsync_ReturnsOk_WhenAuthorizationRecordIsPresent()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBodies.Enqueue("""
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk\""}]}
            """);
        handler.ResponseBodies.Enqueue("""
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1\""}]}
            """);

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Ok, result.Status);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("contoso.io._report._dmarc.mjco.uk", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task CheckAsync_ReturnsMissingAuthorizationRecord_WhenAuthorizationRecordIsAbsent()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBodies.Enqueue("""
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk\""}]}
            """);
        handler.ResponseBodies.Enqueue(NxDomainResponse);

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.MissingAuthorizationRecord, result.Status);
    }

    [Fact]
    public async Task CheckAsync_ParsesMultiSegmentTxtRecordValues()
    {
        var (checker, handler) = CreateChecker();
        // A long TXT value split across two quoted segments, as Cloudflare's JSON API returns for
        // records over 255 bytes.
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; \" \"rua=mailto:rua.dmarc@mjco.uk\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Ok, result.Status);
    }

    [Fact]
    public async Task CheckAsync_MatchesRuaAddress_AmongMultipleCommaSeparatedAddresses()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:other@example.com,mailto:rua.dmarc@mjco.uk\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Ok, result.Status);
    }

    [Fact]
    public async Task CheckAsync_SkipsCnameRecord_AndReadsTheTxtRecordFurtherInTheAnswerArray()
    {
        var (checker, handler) = CreateChecker();
        // A DMARC-hosting-provider setup: _dmarc.contoso.io is a CNAME (type 5) to a
        // provider-managed name, and Cloudflare returns the full chain — CNAME first, TXT second.
        // The CNAME's data is not a valid DMARC string, so taking Answer[0] unfiltered would
        // misread this as Misconfigured.
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":5,"data":"contoso.io._dmarc.provider.net."},{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Ok, result.Status);
    }

    [Fact]
    public async Task CheckAsync_StripsRfc7489SizeLimitSuffix_FromRuaAddress()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk!10m\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Ok, result.Status);
    }

    [Fact]
    public async Task CheckAsync_SetsDnsJsonAcceptHeader_OnEveryRequest()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = NxDomainResponse;

        await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Contains(handler.Requests[0].Headers.Accept, h => h.MediaType == "application/dns-json");
    }
}
