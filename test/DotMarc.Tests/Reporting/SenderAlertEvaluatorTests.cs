using DotMarc.Data;
using DotMarc.Reporting;
using Xunit;

namespace DotMarc.Tests.Reporting;

public sealed class SenderAlertEvaluatorTests
{
    private const int DomainId = 7;
    private const int GroupId = 3;
    private const string MailchimpIp = "198.51.100.7";
    private const string OtherMailchimpIp = "198.51.100.8";
    private const string UnrecognisedIp = "203.0.113.9";

    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static AuthDetailSummary MailchimpSigned => new(DmarcAuthMechanism.Dkim, "bounce.mcsv.net", DmarcMechanismResult.Pass);

    private static ReportRecord Mail(string ip, int count, DispositionResult disposition = DispositionResult.None, bool passing = true, params AuthDetailSummary[] details)
    {
        var record = new ReportRecord
        {
            SourceIp = ip, MessageCount = count, Disposition = disposition, HeaderFrom = "example.com",
            SpfResult = passing ? AuthResult.Pass : AuthResult.Fail, DkimResult = AuthResult.Fail,
        };
        foreach (var detail in details)
        {
            record.AuthDetails.Add(new ReportRecordAuthDetail { Mechanism = detail.Mechanism, Domain = detail.Domain, Result = detail.Result, Selector = detail.Selector });
        }

        return record;
    }

    private static Report ReportOf(DateTimeOffset received, params ReportRecord[] records) =>
        new() { ReportingOrg = "receiver.example", ReportId = Guid.NewGuid().ToString(), RawXml = "<feedback/>", ReceivedUtc = received, Records = [.. records] };

    private static SenderDecision Decision(SenderDecisionScope scope, SenderDecisionStatus status, string key, SenderDecisionKind kind = SenderDecisionKind.Service) => new()
    {
        Scope = scope, Kind = kind, Key = key, Status = status, DecidedBy = "u", DecidedByName = "Sam Lee",
        DomainId = scope == SenderDecisionScope.Domain ? DomainId : null, GroupId = scope == SenderDecisionScope.Group ? GroupId : null,
    };

    private static SenderAlertInputs Inputs(
        IReadOnlyList<Report> reports, IReadOnlyList<SenderDecision>? decisions = null, IEnumerable<string>? newIps = null,
        IReadOnlyDictionary<string, string?>? owners = null, int newMinimum = 20, int deliveredMinimum = 10) =>
        new(DomainId, [GroupId], reports, decisions ?? [], owners ?? new Dictionary<string, string?>(), (newIps ?? []).ToHashSet(), new SenderAlertSettings(newMinimum, deliveredMinimum));

    private static Report MailchimpReport(int count, DispositionResult disposition = DispositionResult.None, bool passing = true, string ip = MailchimpIp) =>
        ReportOf(Now.AddDays(-1), Mail(ip, count, disposition, passing, MailchimpSigned));

    // ---- New and unreviewed ----

    [Fact]
    public void ANewUnreviewedService_ReachingTheMinimumVolume_IsFound()
    {
        var result = SenderAlertEvaluator.Evaluate(Inputs([MailchimpReport(25)], newIps: [MailchimpIp]));

        Assert.Equal(new SenderAlertFinding("Mailchimp", 25), Assert.Single(result.NewUnreviewed));
    }

    [Fact]
    public void ANewSenderBelowTheMinimumVolume_IsNot() =>
        Assert.Empty(SenderAlertEvaluator.Evaluate(Inputs([MailchimpReport(19)], newIps: [MailchimpIp])).NewUnreviewed);

    [Theory]
    [InlineData(SenderDecisionScope.Domain, SenderDecisionStatus.Authorised)]
    [InlineData(SenderDecisionScope.Group, SenderDecisionStatus.Unauthorised)]
    [InlineData(SenderDecisionScope.Baseline, SenderDecisionStatus.Authorised)]
    [InlineData(SenderDecisionScope.Domain, SenderDecisionStatus.UnderReview)]
    public void AServiceWithADecisionAtAnyLevel_IsNotFound(SenderDecisionScope scope, SenderDecisionStatus status) =>
        Assert.Empty(SenderAlertEvaluator.Evaluate(Inputs([MailchimpReport(50)], [Decision(scope, status, "Mailchimp")], [MailchimpIp])).NewUnreviewed);

    [Fact]
    public void AServiceThatIsNotNew_IsNotFound() =>
        Assert.Empty(SenderAlertEvaluator.Evaluate(Inputs([MailchimpReport(50)], newIps: [])).NewUnreviewed);

    [Fact]
    public void AServiceWhereOnlySomeIpsAreNew_IsNotFound()
    {
        var reports = new[] { ReportOf(Now.AddDays(-1), Mail(MailchimpIp, 30, details: MailchimpSigned), Mail(OtherMailchimpIp, 30, details: MailchimpSigned)) };

        Assert.Empty(SenderAlertEvaluator.Evaluate(Inputs(reports, newIps: [OtherMailchimpIp])).NewUnreviewed);
    }

    [Fact]
    public void AnUnrecognisedNewIp_IsJudgedOnItsOwn_ByItsIpDecision()
    {
        var reports = new[] { ReportOf(Now.AddDays(-1), Mail(UnrecognisedIp, 40)) };
        var underReview = Decision(SenderDecisionScope.Domain, SenderDecisionStatus.UnderReview, UnrecognisedIp, SenderDecisionKind.Ip);

        Assert.Equal(new SenderAlertFinding(UnrecognisedIp, 40), Assert.Single(SenderAlertEvaluator.Evaluate(Inputs(reports, newIps: [UnrecognisedIp])).NewUnreviewed));
        Assert.Empty(SenderAlertEvaluator.Evaluate(Inputs(reports, [underReview], [UnrecognisedIp])).NewUnreviewed);
    }

    [Fact]
    public void AnAlignedMicrosoftSender_FoundByOwner_IsNamedAsTheService_NotAsAnIp()
    {
        var microsoftIp = "40.107.0.1";
        var reports = new[] { ReportOf(Now.AddDays(-1), Mail(microsoftIp, 60, details: new AuthDetailSummary(DmarcAuthMechanism.Dkim, "example.com", DmarcMechanismResult.Pass, "selector1"))) };
        var owners = new Dictionary<string, string?> { [microsoftIp] = "Microsoft Corporation" };

        var result = SenderAlertEvaluator.Evaluate(Inputs(reports, newIps: [microsoftIp], owners: owners));

        Assert.Equal("Microsoft 365", Assert.Single(result.NewUnreviewed).Sender);
    }

    // ---- Unauthorised and delivered ----

    private static readonly SenderDecision UnauthorisedMailchimp = Decision(SenderDecisionScope.Domain, SenderDecisionStatus.Unauthorised, "Mailchimp");

    [Fact]
    public void AnUnauthorisedService_WithDeliveredMailAtOrAboveTheMinimum_IsFound()
    {
        var atMinimum = SenderAlertEvaluator.Evaluate(Inputs([MailchimpReport(10, passing: false)], [UnauthorisedMailchimp]));
        var below = SenderAlertEvaluator.Evaluate(Inputs([MailchimpReport(9, passing: false)], [UnauthorisedMailchimp]));

        Assert.Equal(new SenderAlertFinding("Mailchimp", 10), Assert.Single(atMinimum.UnauthorisedDelivered));
        Assert.Empty(below.UnauthorisedDelivered);
    }

    [Fact]
    public void Delivered_MeansDispositionNone_WhetherItPassesOrFails()
    {
        var reports = new[]
        {
            ReportOf(Now.AddDays(-1),
                Mail(MailchimpIp, 6, DispositionResult.None, passing: true, MailchimpSigned),
                Mail(MailchimpIp, 6, DispositionResult.None, passing: false, MailchimpSigned),
                Mail(MailchimpIp, 100, DispositionResult.Reject, passing: false, MailchimpSigned)),
        };

        var finding = Assert.Single(SenderAlertEvaluator.Evaluate(Inputs(reports, [UnauthorisedMailchimp])).UnauthorisedDelivered);

        Assert.Equal(12, finding.Messages);
    }

    [Fact]
    public void BlockedMail_IsNotCounted()
    {
        var reports = new[] { ReportOf(Now.AddDays(-1), Mail(MailchimpIp, 500, DispositionResult.Reject, false, MailchimpSigned), Mail(MailchimpIp, 500, DispositionResult.Quarantine, false, MailchimpSigned)) };

        Assert.Empty(SenderAlertEvaluator.Evaluate(Inputs(reports, [UnauthorisedMailchimp])).UnauthorisedDelivered);
    }

    [Theory]
    [InlineData(SenderDecisionStatus.Authorised)]
    [InlineData(SenderDecisionStatus.UnderReview)]
    public void AnAuthorisedOrUnderReviewService_IsNeverFoundAsUnauthorised(SenderDecisionStatus status) =>
        Assert.Empty(SenderAlertEvaluator.Evaluate(Inputs([MailchimpReport(500, passing: false)], [Decision(SenderDecisionScope.Domain, status, "Mailchimp")])).UnauthorisedDelivered);

    [Fact]
    public void AnUnreviewedService_IsNeverFoundAsUnauthorised() =>
        Assert.Empty(SenderAlertEvaluator.Evaluate(Inputs([MailchimpReport(500, passing: false)])).UnauthorisedDelivered);

    [Fact]
    public void AnUnauthorisedIpDecision_OnAnUnrecognisedSource_IsFound()
    {
        var reports = new[] { ReportOf(Now.AddDays(-1), Mail(UnrecognisedIp, 30)) };
        var decision = Decision(SenderDecisionScope.Domain, SenderDecisionStatus.Unauthorised, UnrecognisedIp, SenderDecisionKind.Ip);

        Assert.Equal(new SenderAlertFinding(UnrecognisedIp, 30), Assert.Single(SenderAlertEvaluator.Evaluate(Inputs(reports, [decision])).UnauthorisedDelivered));
    }

    [Fact]
    public void AnAuthorisedIpDecision_OverridesAnUnauthorisedService_ForThatIpOnly()
    {
        var reports = new[]
        {
            ReportOf(Now.AddDays(-1), Mail(MailchimpIp, 40, details: MailchimpSigned), Mail(OtherMailchimpIp, 25, details: MailchimpSigned)),
        };
        var authorisedIp = Decision(SenderDecisionScope.Domain, SenderDecisionStatus.Authorised, MailchimpIp, SenderDecisionKind.Ip);

        var finding = Assert.Single(SenderAlertEvaluator.Evaluate(Inputs(reports, [UnauthorisedMailchimp, authorisedIp])).UnauthorisedDelivered);

        Assert.Equal(25, finding.Messages);
    }

    [Fact]
    public void Findings_AreBusiestFirst()
    {
        var reports = new[]
        {
            ReportOf(Now.AddDays(-1), Mail("198.51.100.20", 30, details: new AuthDetailSummary(DmarcAuthMechanism.Dkim, "bounce.sendgrid.net", DmarcMechanismResult.Pass)), Mail(MailchimpIp, 90, details: MailchimpSigned)),
        };

        var result = SenderAlertEvaluator.Evaluate(Inputs(reports, newIps: ["198.51.100.20", MailchimpIp]));

        Assert.Equal(["Mailchimp", "SendGrid"], result.NewUnreviewed.Select(finding => finding.Sender));
    }

    // ---- Which sources might be new ----

    [Fact]
    public void NewSourceCandidates_ReturnsOnlyIpsWhoseWholeWindowIsRecent()
    {
        var sevenDaysAgo = Now.AddDays(-7);
        var reports = new[]
        {
            ReportOf(Now.AddDays(-20), Mail("192.0.2.1", 10)),
            ReportOf(Now.AddDays(-2), Mail("192.0.2.1", 10), Mail("192.0.2.2", 10)),
            ReportOf(Now.AddDays(-1), Mail("192.0.2.3", 10)),
        };

        var candidates = SenderAlertEvaluator.NewSourceCandidates(reports, sevenDaysAgo);

        Assert.Equal(["192.0.2.2", "192.0.2.3"], candidates.Order());
    }

    // ---- The messages ----

    [Fact]
    public void TheNewSenderMessage_StatesTheCauseFirst_ThenWhatToDo_WithThousandsSeparators()
    {
        var text = SenderAlertMessages.NewSenders("example.com", [new("Mailchimp", 1200), new("203.0.113.9", 40)]);

        Assert.Equal(
            "example.com has 2 new senders that nobody has reviewed: Mailchimp (1,200 messages), 203.0.113.9 (40 messages). " +
            "Open the domain's Sources tab and mark each one Authorised, Unauthorised or Under review.",
            text);
    }

    [Fact]
    public void TheNewSenderMessage_HasASingularForm() =>
        Assert.Equal(
            "example.com has 1 new sender that nobody has reviewed: Mailchimp (1 message). Open the domain's Sources tab and mark it Authorised, Unauthorised or Under review.",
            SenderAlertMessages.NewSenders("example.com", [new("Mailchimp", 1)]));

    [Theory]
    [InlineData(DmarcPolicyLevel.None, "Your policy is none, so this mail is still delivered. Move to quarantine or reject to block it.")]
    [InlineData(DmarcPolicyLevel.Quarantine, "Your policy is quarantine, so receivers should send it to spam.")]
    [InlineData(DmarcPolicyLevel.Reject, "Your policy is reject, so receivers should be rejecting it.")]
    public void TheDeliveredMessage_NamesTheSenders_AndEndsWithThePolicyLine(DmarcPolicyLevel policy, string policyLine)
    {
        Assert.Equal(
            $"Mail from senders marked unauthorised is being delivered for example.com: Mailchimp (500 messages), SendGrid (12 messages). {policyLine}",
            SenderAlertMessages.UnauthorisedDelivered("example.com", [new("Mailchimp", 500), new("SendGrid", 12)], policy));
        Assert.Equal(
            $"Mail from a sender marked unauthorised is being delivered for example.com: Mailchimp (500 messages). {policyLine}",
            SenderAlertMessages.UnauthorisedDelivered("example.com", [new("Mailchimp", 500)], policy));
    }

    [Fact]
    public void TheTitles_AreFixed()
    {
        Assert.Equal("New senders to review", SenderAlertMessages.NewSendersTitle);
        Assert.Equal("Mail from an unauthorised sender is being delivered", SenderAlertMessages.UnauthorisedDeliveredTitle);
    }
}
