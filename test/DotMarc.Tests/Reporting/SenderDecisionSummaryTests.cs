using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.Reporting;
using Xunit;

namespace DotMarc.Tests.Reporting;

public sealed class SenderDecisionSummaryTests
{
    private const int DomainId = 7;
    private const int GroupId = 3;
    private const int OtherGroupId = 4;
    private const string MailchimpIp = "198.51.100.7";
    private const string OtherMailchimpIp = "198.51.100.8";
    private const string UnrecognisedIp = "203.0.113.9";

    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static AuthDetailSummary MailchimpSigned => new(DmarcAuthMechanism.Dkim, "bounce.mcsv.net", DmarcMechanismResult.Pass);

    private static ReportRecord Mail(string ip, int count, DispositionResult disposition = DispositionResult.None, bool passing = true, bool mailchimp = true)
    {
        var record = new ReportRecord
        {
            SourceIp = ip, MessageCount = count, Disposition = disposition, HeaderFrom = "example.com",
            SpfResult = passing ? AuthResult.Pass : AuthResult.Fail, DkimResult = AuthResult.Fail,
        };
        if (mailchimp)
        {
            var detail = MailchimpSigned;
            record.AuthDetails.Add(new ReportRecordAuthDetail { Mechanism = detail.Mechanism, Domain = detail.Domain, Result = detail.Result, Selector = detail.Selector });
        }

        return record;
    }

    private static IReadOnlyList<Report> ReportsOf(params ReportRecord[] records) =>
        [new Report { ReportingOrg = "receiver.example", ReportId = Guid.NewGuid().ToString(), RawXml = "<feedback/>", ReceivedUtc = Now.AddDays(-1), Records = [.. records] }];

    private static SenderDecision Decision(SenderDecisionScope scope, SenderDecisionStatus status, string key, SenderDecisionKind kind = SenderDecisionKind.Service, int groupId = GroupId) => new()
    {
        Scope = scope, Kind = kind, Key = key, Status = status, DecidedBy = "u", DecidedByName = "Sam Lee",
        DomainId = scope == SenderDecisionScope.Domain ? DomainId : null, GroupId = scope == SenderDecisionScope.Group ? groupId : null,
    };

    private static SenderDecisionSummary Summarise(IReadOnlyList<Report> reports, params SenderDecision[] decisions) =>
        SenderDecisionSummaries.Summarise(reports, new Dictionary<string, string?>(), decisions, DomainId, [GroupId, OtherGroupId]);

    [Fact]
    public void Summarise_CountsAServiceWithNoDecisionAsOneUnreviewedSender() =>
        Assert.Equal(1, Summarise(ReportsOf(Mail(MailchimpIp, 5), Mail(OtherMailchimpIp, 5))).UnreviewedSenders);

    [Fact]
    public void Summarise_CountsAnUnmatchedIpAsItsOwnUnreviewedSender() =>
        Assert.Equal(1, Summarise(ReportsOf(Mail(UnrecognisedIp, 5, mailchimp: false))).UnreviewedSenders);

    [Theory]
    [InlineData(SenderDecisionStatus.Authorised)]
    [InlineData(SenderDecisionStatus.UnderReview)]
    [InlineData(SenderDecisionStatus.Unauthorised)]
    public void Summarise_DoesNotCountAnyDecisionAsUnreviewed(SenderDecisionStatus status) =>
        Assert.Equal(0, Summarise(ReportsOf(Mail(MailchimpIp, 5)), Decision(SenderDecisionScope.Domain, status, "Mailchimp")).UnreviewedSenders);

    [Fact]
    public void Summarise_BaselineDecisionClearsTheSenderOnEveryDomain() =>
        Assert.Equal(0, Summarise(ReportsOf(Mail(MailchimpIp, 5)), Decision(SenderDecisionScope.Baseline, SenderDecisionStatus.Authorised, "Mailchimp")).UnreviewedSenders);

    [Fact]
    public void Summarise_SplitsUnauthorisedMailIntoBlockedAndDelivered()
    {
        var reports = ReportsOf(
            Mail(MailchimpIp, 20, DispositionResult.Quarantine, passing: false),
            Mail(MailchimpIp, 10, DispositionResult.Reject, passing: false),
            Mail(MailchimpIp, 12, DispositionResult.None, passing: false),
            Mail(MailchimpIp, 8, DispositionResult.None, passing: true));

        var summary = Summarise(reports, Decision(SenderDecisionScope.Domain, SenderDecisionStatus.Unauthorised, "Mailchimp"));

        Assert.Equal(30, summary.UnauthorisedBlocked);
        Assert.Equal(20, summary.UnauthorisedDelivered);
        Assert.Equal(12, summary.UnauthorisedDeliveredFailing);
    }

    [Theory]
    [InlineData(SenderDecisionStatus.Authorised)]
    [InlineData(SenderDecisionStatus.UnderReview)]
    public void Summarise_IgnoresMailFromAuthorisedAndUnderReviewSenders(SenderDecisionStatus status)
    {
        var reports = ReportsOf(Mail(MailchimpIp, 12, DispositionResult.None, passing: false), Mail(MailchimpIp, 4, DispositionResult.Reject, passing: false));

        var summary = Summarise(reports, Decision(SenderDecisionScope.Domain, status, "Mailchimp"));

        Assert.Equal((0L, 0L, 0L), (summary.UnauthorisedBlocked, summary.UnauthorisedDelivered, summary.UnauthorisedDeliveredFailing));
    }

    [Fact]
    public void Summarise_AnIpDecisionOverridesItsService()
    {
        var reports = ReportsOf(Mail(MailchimpIp, 5), Mail(OtherMailchimpIp, 50));

        var summary = Summarise(reports,
            Decision(SenderDecisionScope.Domain, SenderDecisionStatus.Authorised, "Mailchimp"),
            Decision(SenderDecisionScope.Domain, SenderDecisionStatus.Unauthorised, MailchimpIp, SenderDecisionKind.Ip));

        Assert.Equal(5, summary.UnauthorisedDelivered);
    }

    [Fact]
    public void Summarise_ConflictingGroupDecisionsResolveToTheMostCautious()
    {
        var summary = Summarise(ReportsOf(Mail(MailchimpIp, 9)),
            Decision(SenderDecisionScope.Group, SenderDecisionStatus.Authorised, "Mailchimp", groupId: GroupId),
            Decision(SenderDecisionScope.Group, SenderDecisionStatus.Unauthorised, "Mailchimp", groupId: OtherGroupId));

        Assert.Equal(9, summary.UnauthorisedDelivered);
    }

    [Fact]
    public void Summarise_ReturnsZerosForNoReports() =>
        Assert.Equal(new SenderDecisionSummary(0, 0, 0, 0), Summarise([]));

    private static readonly SpfServiceRange[] MailchimpRange = [new("Mailchimp", "servers.mcsv.net", "203.0.113.0/24")];

    [Fact]
    public void Summarise_AnIpInASpfRange_TakesItsServicesDecision()
    {
        var reports = ReportsOf(Mail(UnrecognisedIp, 30, DispositionResult.Quarantine, passing: false, mailchimp: false), Mail(UnrecognisedIp, 6, mailchimp: false));

        var summary = SenderDecisionSummaries.Summarise(
            reports, new Dictionary<string, string?>(), [Decision(SenderDecisionScope.Domain, SenderDecisionStatus.Unauthorised, "Mailchimp")],
            DomainId, [GroupId], MailchimpRange);

        Assert.Equal((30L, 6L, 0), (summary.UnauthorisedBlocked, summary.UnauthorisedDelivered, summary.UnreviewedSenders));
    }

    [Fact]
    public void Summarise_AnIpInASpfRange_CountsOnceAsTheServiceWhenUnreviewed()
    {
        var reports = ReportsOf(Mail(UnrecognisedIp, 5, mailchimp: false), Mail(MailchimpIp, 5, mailchimp: false));

        var summary = SenderDecisionSummaries.Summarise(reports, new Dictionary<string, string?>(), [], DomainId, [GroupId], [new SpfServiceRange("Mailchimp", "servers.mcsv.net", "203.0.113.0/24"), new SpfServiceRange("Mailchimp", "servers.mcsv.net", "198.51.100.0/24")]);

        Assert.Equal(1, summary.UnreviewedSenders);
    }
}
