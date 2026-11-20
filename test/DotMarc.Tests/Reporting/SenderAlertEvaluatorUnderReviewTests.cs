using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Reporting;
using Xunit;

namespace DotMarc.Tests.Reporting;

/// <summary>What Under review means for the new sender alert: decided (today's behaviour), unreviewed, or decided for a while and then
/// alerting again. A sender that has stayed Under review past the window is no longer new, so alerting again does not need it to be.</summary>
public sealed class SenderAlertEvaluatorUnderReviewTests
{
    private const int DomainId = 7;
    private const int GroupId = 3;
    private const string MailchimpIp = "198.51.100.7";
    private const string UnrecognisedIp = "203.0.113.9";

    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly UnderReviewHandling After14Days = new(UnderReviewMode.AlertAgainAfterDays, 14);

    private static ReportRecord Mail(string ip, int count, bool signedByMailchimp)
    {
        var record = new ReportRecord
        {
            SourceIp = ip, MessageCount = count, Disposition = DispositionResult.None, HeaderFrom = "example.com", SpfResult = AuthResult.Pass, DkimResult = AuthResult.Fail
        };
        if (signedByMailchimp)
        {
            record.AuthDetails.Add(new ReportRecordAuthDetail { Mechanism = DmarcAuthMechanism.Dkim, Domain = "bounce.mcsv.net", Result = DmarcMechanismResult.Pass });
        }

        return record;
    }

    private static Report ReportOf(params ReportRecord[] records) =>
        new() { ReportingOrg = "receiver.example", ReportId = Guid.NewGuid().ToString(), RawXml = "<feedback/>", ReceivedUtc = Now.AddDays(-1), Records = [.. records] };

    private static SenderDecision UnderReview(SenderDecisionScope scope, string key, TimeSpan decidedAgo, SenderDecisionKind kind = SenderDecisionKind.Service) => new()
    {
        Scope = scope, Kind = kind, Key = key, Status = SenderDecisionStatus.UnderReview, DecidedBy = "u", DecidedByName = "Sam Lee",
        DecidedUtc = Now - decidedAgo,
        DomainId = scope == SenderDecisionScope.Domain ? DomainId : null, GroupId = scope == SenderDecisionScope.Group ? GroupId : null,
    };

    private static SenderAlertResult Evaluate(
        IReadOnlyList<Report> reports, IReadOnlyList<SenderDecision> decisions, UnderReviewHandling? handling, bool isNew)
    {
        var inputs = new SenderAlertInputs(
            DomainId, [GroupId], reports, decisions, new Dictionary<string, string?>(),
            (isNew ? new[] { MailchimpIp, UnrecognisedIp } : []).ToHashSet(), new SenderAlertSettings(20, 10), UnderReview: handling, NowUtc: Now);
        return SenderAlertEvaluator.Evaluate(inputs);
    }

    private static Report MailchimpReport(int count = 30) => ReportOf(Mail(MailchimpIp, count, signedByMailchimp: true));

    [Fact]
    public void CountsAsDecided_IsTodaysBehaviour_ForNewAndAgedSendersAlike()
    {
        var decisions = new[] { UnderReview(SenderDecisionScope.Domain, "Mailchimp", TimeSpan.FromDays(40)) };

        Assert.Empty(Evaluate([MailchimpReport()], decisions, handling: null, isNew: true).NewUnreviewed);
        Assert.Empty(Evaluate([MailchimpReport()], decisions, UnderReviewHandling.Default, isNew: false).NewUnreviewed);
    }

    [Fact]
    public void CountsAsUnreviewed_AlertsForANewUnderReviewService_ButNotAnAgedOne()
    {
        var decisions = new[] { UnderReview(SenderDecisionScope.Domain, "Mailchimp", TimeSpan.FromDays(2)) };
        var handling = new UnderReviewHandling(UnderReviewMode.CountsAsUnreviewed);

        var found = Assert.Single(Evaluate([MailchimpReport()], decisions, handling, isNew: true).NewUnreviewed);
        Assert.Equal(new SenderAlertFinding("Mailchimp", 30, UnderReview: true), found);
        Assert.Empty(Evaluate([MailchimpReport()], decisions, handling, isNew: false).NewUnreviewed);
    }

    [Fact]
    public void AfterDays_DoesNotAlertBeforeTheWindow()
    {
        var decisions = new[] { UnderReview(SenderDecisionScope.Domain, "Mailchimp", TimeSpan.FromDays(14) - TimeSpan.FromSeconds(1)) };

        Assert.Empty(Evaluate([MailchimpReport()], decisions, After14Days, isNew: false).NewUnreviewed);
    }

    [Fact]
    public void AfterDays_AlertsExactlyAtTheWindow_WithoutTheSenderBeingNew()
    {
        var decisions = new[] { UnderReview(SenderDecisionScope.Domain, "Mailchimp", TimeSpan.FromDays(14)) };

        var found = Assert.Single(Evaluate([MailchimpReport()], decisions, After14Days, isNew: false).NewUnreviewed);

        Assert.Equal(new SenderAlertFinding("Mailchimp", 30, UnderReview: true), found);
    }

    [Theory]
    [InlineData(SenderDecisionScope.Group)]
    [InlineData(SenderDecisionScope.Baseline)]
    public void AfterDays_UsesAnInheritedDecisionsOwnDate(SenderDecisionScope scope)
    {
        var decisions = new[] { UnderReview(scope, "Mailchimp", TimeSpan.FromDays(30)) };

        Assert.Single(Evaluate([MailchimpReport()], decisions, After14Days, isNew: false).NewUnreviewed);
    }

    [Fact]
    public void AfterDays_StillNeedsTheMinimumVolume()
    {
        var decisions = new[] { UnderReview(SenderDecisionScope.Domain, "Mailchimp", TimeSpan.FromDays(30)) };

        Assert.Empty(Evaluate([MailchimpReport(count: 19)], decisions, After14Days, isNew: false).NewUnreviewed);
    }

    [Fact]
    public void AfterDays_WorksForAnUnrecognisedIpDecidedByItsOwn()
    {
        var decisions = new[] { UnderReview(SenderDecisionScope.Domain, UnrecognisedIp, TimeSpan.FromDays(20), SenderDecisionKind.Ip) };
        var reports = new[] { ReportOf(Mail(UnrecognisedIp, 50, signedByMailchimp: false)) };

        var found = Assert.Single(Evaluate(reports, decisions, After14Days, isNew: false).NewUnreviewed);

        Assert.Equal(new SenderAlertFinding(UnrecognisedIp, 50, UnderReview: true), found);
    }

    [Theory]
    [InlineData(SenderDecisionStatus.Authorised)]
    [InlineData(SenderDecisionStatus.Unauthorised)]
    public void ADecidedSender_NeverAlerts_WhateverTheMode(SenderDecisionStatus status)
    {
        var decision = UnderReview(SenderDecisionScope.Domain, "Mailchimp", TimeSpan.FromDays(60));
        decision.Status = status;

        foreach (var handling in new[] { UnderReviewHandling.Default, new UnderReviewHandling(UnderReviewMode.CountsAsUnreviewed), After14Days })
        {
            Assert.Empty(Evaluate([MailchimpReport()], [decision], handling, isNew: true).NewUnreviewed);
        }
    }

    [Fact]
    public void ASenderWithNoDecision_StillAlertsWhenNew_InEveryMode()
    {
        foreach (var handling in new[] { UnderReviewHandling.Default, new UnderReviewHandling(UnderReviewMode.CountsAsUnreviewed), After14Days })
        {
            var found = Assert.Single(Evaluate([MailchimpReport()], [], handling, isNew: true).NewUnreviewed);
            Assert.False(found.UnderReview);
        }
    }

    [Fact]
    public void TheMessage_SaysWhichSendersAreStillUnderReview()
    {
        var message = SenderAlertMessages.NewSenders("example.com", [new SenderAlertFinding("SendGrid", 50), new SenderAlertFinding("Mailchimp", 30, UnderReview: true)]);

        Assert.Contains("1 new sender that nobody has reviewed: SendGrid (50 messages)", message);
        Assert.Contains("1 sender still under review: Mailchimp (30 messages)", message);
    }

    [Fact]
    public void TheMessage_ForOnlyNewSenders_IsUnchanged()
    {
        var message = SenderAlertMessages.NewSenders("example.com", [new SenderAlertFinding("SendGrid", 50)]);

        Assert.Equal(
            "example.com has 1 new sender that nobody has reviewed: SendGrid (50 messages). Open the domain's Sources tab and mark it Authorised, Unauthorised or Under review.",
            message);
    }

    [Theory]
    [InlineData(null, 0, UnderReviewMode.CountsAsDecided, 0)]
    [InlineData("{\"underReview\":\"decided\"}", 0, UnderReviewMode.CountsAsDecided, 0)]
    [InlineData("{\"underReview\":\"unreviewed\"}", 0, UnderReviewMode.CountsAsUnreviewed, 0)]
    [InlineData("{\"underReview\":\"afterDays\",\"underReviewDays\":\"21\"}", 0, UnderReviewMode.AlertAgainAfterDays, 21)]
    [InlineData("{\"underReview\":\"afterDays\"}", 0, UnderReviewMode.AlertAgainAfterDays, 14)]
    [InlineData("{\"underReview\":\"nonsense\"}", 0, UnderReviewMode.CountsAsDecided, 0)]
    public void TheRulesParameters_ReadAsAnUnderReviewHandling(string? json, int unused, UnderReviewMode mode, int days)
    {
        _ = unused;

        var handling = new AlertRuleParameters(json).UnderReview();

        Assert.Equal(new UnderReviewHandling(mode, days), handling);
    }
}
