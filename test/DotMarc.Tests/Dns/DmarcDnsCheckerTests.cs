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
    public async Task CheckAsync_SaysNoDmarcRecord_WhenOtherTxtValuesSitAtTheName()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = TxtAnswers("google-site-verification=abc");

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.MissingOwnRecord, result.Status);
        Assert.Equal("No DMARC record found at _dmarc.contoso.io", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMisconfigured_WhenTheVersionIsNotDMARC1()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC2; p=none\""}]}
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
    public async Task CheckAsync_ReturnsOk_AndMakesOnlyOneRequest_RegardlessOfMailboxDomain()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Ok, result.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CheckAsync_ReturnsOk_EvenWhenTheAuthorizationRecordWouldBeMissing()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Ok, result.Status);
        Assert.Single(handler.Requests);
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
    public async Task CheckAsync_ReturnsOtherReportAddresses_WhenOneOtherAddressSitsBesideTheMailbox()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:other@example.com,mailto:rua.dmarc@mjco.uk\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.OtherReportAddresses, result.Status);
        Assert.Contains("other@example.com", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMisconfigured_WhenAnAddressLacksTheMailtoScheme()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=reject; rua=mailto:rua.dmarc@mjco.uk,0329c347d460.a@dmarcinput.com; ruf=mailto:0329c347d460.f@dmarcinput.com; sp=reject; fo=1\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Misconfigured, result.Status);
        Assert.Contains("0329c347d460.a@dmarcinput.com", result.Detail);
        Assert.Contains("mailto:", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_ReturnsOtherReportAddresses_WhenTheMailboxIsNotTheFirstAddress()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk,mailto:other@example.com\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.OtherReportAddresses, result.Status);
    }

    [Fact]
    public async Task CheckAsync_ReturnsTooManyReportAddresses_WhenMoreThanTwoAddressesAreListed()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:a@example.com,mailto:b@example.com,mailto:rua.dmarc@mjco.uk\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.TooManyReportAddresses, result.Status);
        Assert.Contains("3 addresses", result.Detail);
        Assert.Contains("rua.dmarc@mjco.uk", result.Detail);
        Assert.Equal(DmarcPolicyLevel.Quarantine, result.Policy!.Policy);
    }

    [Fact]
    public async Task CheckAsync_CountsTheSameAddressOnce_EvenIfItIsListedTwice()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk,mailto:RUA.dmarc@mjco.uk!10m\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Ok, result.Status);
    }

    [Fact]
    public async Task CheckAsync_StaysMisconfigured_WhenSeveralAddressesDoNotIncludeTheMailbox()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:a@example.com,mailto:b@example.com,mailto:c@example.com\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Misconfigured, result.Status);
    }

    [Fact]
    public async Task CheckAsync_SkipsCnameRecord_AndReadsTheTxtRecordFurtherInTheAnswerArray()
    {
        var (checker, handler) = CreateChecker();
        // A DMARC-hosting-provider setup: _dmarc.contoso.io is a CNAME (type 5) to a
        // provider-managed name, and Cloudflare returns the full chain - CNAME first, TXT second.
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

    [Fact]
    public async Task CheckAuthorizationAsync_ReturnsNotApplicable_WhenMailboxDomainMatchesMonitoredDomain()
    {
        var (checker, _) = CreateChecker();

        var result = await checker.CheckAuthorizationAsync("contoso.io", "dmarc@contoso.io", CancellationToken.None);

        Assert.Equal(DmarcAuthorizationCheckStatus.NotApplicable, result.Status);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task CheckAuthorizationAsync_ReturnsMissing_WhenNoAuthorizationRecordExists()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = NxDomainResponse;

        var result = await checker.CheckAuthorizationAsync("contoso.io", "dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcAuthorizationCheckStatus.Missing, result.Status);
        Assert.Contains("contoso.io._report._dmarc.mjco.uk", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task CheckAuthorizationAsync_ReturnsOk_WhenAuthorizationRecordExists()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1;\""}]}
            """;

        var result = await checker.CheckAuthorizationAsync("contoso.io", "dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcAuthorizationCheckStatus.Ok, result.Status);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task CheckAsync_ReadsThePolicy_WhenTheRecordIsOk()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; pct=50; rua=mailto:rua.dmarc@mjco.uk\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Ok, result.Status);
        Assert.Equal(new DmarcPolicyTags(DmarcPolicyLevel.Quarantine, DmarcPolicyLevel.Quarantine, 50), result.Policy);
    }

    [Fact]
    public async Task CheckAsync_ReadsThePolicy_EvenWhenRuaIsWrong()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=reject; rua=mailto:other@example.com\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Misconfigured, result.Status);
        Assert.Equal(DmarcPolicyLevel.Reject, result.Policy!.Policy);
    }

    [Fact]
    public async Task CheckAsync_HasNoPolicy_WithoutARecord()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = NxDomainResponse;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Null(result.Policy);
    }

    private static string TxtAnswers(params string[] txtValues) =>
        "{\"Status\":0,\"Answer\":[" + string.Join(",", txtValues.Select(value => "{\"type\":16,\"data\":\"\\\"" + value + "\\\"\"}")) + "]}";

    [Theory]
    [InlineData("v=DMARC1; p=none; p=reject; rua=mailto:rua.dmarc@mjco.uk", DmarcRecordProblemKind.RepeatedTag)]
    [InlineData("v=DMARC1; rua=mailto:rua.dmarc@mjco.uk", DmarcRecordProblemKind.MissingPolicy)]
    [InlineData("p=reject; v=DMARC1; rua=mailto:rua.dmarc@mjco.uk", DmarcRecordProblemKind.VersionMissingOrNotFirst)]
    public async Task CheckAsync_ReturnsMisconfigured_NamingTheBreakingProblem(string record, DmarcRecordProblemKind expectedKind)
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = TxtAnswers(record);

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Misconfigured, result.Status);
        var problem = Assert.Single(result.Analysis!.Problems, candidate => candidate.Kind == expectedKind);
        Assert.Contains(problem.Description, result.Detail);
        Assert.NotNull(result.Policy);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMisconfigured_WhenThereAreTwoDmarcRecords()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = TxtAnswers("v=DMARC1; p=none; rua=mailto:rua.dmarc@mjco.uk", "v=DMARC1; p=reject");

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Misconfigured, result.Status);
        Assert.Contains(result.Analysis!.Problems, problem => problem.Kind == DmarcRecordProblemKind.MultipleRecords);
        Assert.Equal(2, result.Analysis.Records.Count);
    }

    [Fact]
    public async Task CheckAsync_KeepsTheRuaStatus_WhenOnlyInformationalProblemsExist()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = TxtAnswers("v=DMARC1; p=reject; pct=100; t=y; rf=afrf; rua=mailto:rua.dmarc@mjco.uk");

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Ok, result.Status);
        Assert.Null(result.Detail);
        Assert.True(result.Analysis!.Testing);
        Assert.Contains(result.Analysis.Problems, problem => problem.Kind == DmarcRecordProblemKind.ObsoleteTag && problem.Tag == "pct");
        Assert.Contains(result.Analysis.Problems, problem => problem.Kind == DmarcRecordProblemKind.TestingMode);
    }

    [Fact]
    public async Task CheckAsync_TreatsADmarcRecordBesideAVerificationValueAsOneRecord()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = TxtAnswers("some-verification=abc123", "v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk");

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Ok, result.Status);
        Assert.Equal(["v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk"], result.Analysis!.Records);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMissingOwnRecord_WhenOnlyUnrelatedValuesExist()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = TxtAnswers("some-verification=abc123");

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.MissingOwnRecord, result.Status);
        Assert.Null(result.Analysis);
    }
}
