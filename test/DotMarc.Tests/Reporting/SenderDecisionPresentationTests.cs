using DotMarc.Data;
using DotMarc.Reporting;
using Xunit;

namespace DotMarc.Tests.Reporting;

public sealed class SenderDecisionPresentationTests
{
    private const string TodaysFix = "TODAYS FIX";
    private const string MadeBy = "Marked unauthorised by Sam Lee on 18 November 2026";

    private static SenderServiceRow Row(long passing, long failing, long actedOn) => new(
        "Mailchimp", false, passing + failing, 0.5, 0.9, null, passing, failing, actedOn, failing > 0 ? SenderFailureCause.Both : null, TodaysFix, [], []);

    private static ResolvedSenderDecision Decided(SenderDecisionStatus status, SenderDecisionLevel level = SenderDecisionLevel.Domain) => new(
        status, level,
        new SenderDecision
        {
            Scope = SenderDecisionScope.Domain, DomainId = 1, Kind = SenderDecisionKind.Service, Key = "Mailchimp", Status = status,
            DecidedBy = "user-1", DecidedByName = "Sam Lee", DecidedUtc = new DateTimeOffset(2026, 11, 18, 9, 30, 0, TimeSpan.Zero),
        },
        null);

    private static readonly ResolvedSenderDecision Unreviewed = new(null, SenderDecisionLevel.None, null, null);

    [Fact]
    public void Unreviewed_AndUnderReview_KeepTodaysFix()
    {
        Assert.Equal(TodaysFix, SenderDecisionPresentation.FixFor(Row(10, 40, 0), Unreviewed, DmarcPolicyLevel.None));
        Assert.Equal(TodaysFix, SenderDecisionPresentation.FixFor(Row(10, 40, 0), Decided(SenderDecisionStatus.UnderReview), DmarcPolicyLevel.None));
    }

    [Fact]
    public void Authorised_WithNothingFailing_HasNoFix() =>
        Assert.Null(SenderDecisionPresentation.FixFor(Row(100, 0, 0), Decided(SenderDecisionStatus.Authorised), DmarcPolicyLevel.Reject));

    [Fact]
    public void Authorised_ButFailing_SaysSoAndKeepsTheFix() =>
        Assert.Equal(
            $"You authorised Mailchimp, but 40 messages fail DMARC. {TodaysFix}",
            SenderDecisionPresentation.FixFor(Row(10, 40, 0), Decided(SenderDecisionStatus.Authorised), DmarcPolicyLevel.None));

    [Fact]
    public void Authorised_FailingCounts_UseThousandsSeparators() =>
        Assert.StartsWith(
            "You authorised Mailchimp, but 1,234 messages fail DMARC.",
            SenderDecisionPresentation.FixFor(Row(0, 1234, 0), Decided(SenderDecisionStatus.Authorised), DmarcPolicyLevel.None));

    [Fact]
    public void Unauthorised_WhenAllFailingMailWasActedOn_SaysThePolicyIsBlockingIt() =>
        Assert.Equal(
            $"{MadeBy}. Your DMARC policy is blocking it.",
            SenderDecisionPresentation.FixFor(Row(0, 40, 40), Decided(SenderDecisionStatus.Unauthorised), DmarcPolicyLevel.Reject));

    [Theory]
    [InlineData(DmarcPolicyLevel.None, "Your policy is none, so this mail is still delivered. Move to quarantine or reject to block it.")]
    [InlineData(DmarcPolicyLevel.Quarantine, "Your policy is quarantine, so receivers should send it to spam.")]
    [InlineData(DmarcPolicyLevel.Reject, "Your policy is reject, so receivers should be rejecting it.")]
    public void Unauthorised_WithSomeMailDelivered_SaysHowMuch_AndWhatThePolicyMeans(DmarcPolicyLevel policy, string policyLine) =>
        Assert.Equal(
            $"{MadeBy}. 25 messages were still delivered. {policyLine}",
            SenderDecisionPresentation.FixFor(Row(0, 40, 15), Decided(SenderDecisionStatus.Unauthorised), policy));

    [Fact]
    public void Unauthorised_ButItsMailPassesDmarc_SaysItAuthenticatesAsTheDomain() =>
        Assert.Equal(
            $"{MadeBy}, but 90 messages from it pass DMARC. It authenticates as your domain, so check whether it should be authorised or remove its access.",
            SenderDecisionPresentation.FixFor(Row(90, 0, 0), Decided(SenderDecisionStatus.Unauthorised), DmarcPolicyLevel.Reject));

    [Fact]
    public void ADecisionWithNoStoredRow_FallsBackToTodaysFix() =>
        Assert.Equal(
            TodaysFix,
            SenderDecisionPresentation.FixFor(Row(0, 40, 0), new ResolvedSenderDecision(SenderDecisionStatus.Unauthorised, SenderDecisionLevel.Domain, null, null), DmarcPolicyLevel.None));

    [Theory]
    [InlineData(null, "Unreviewed")]
    [InlineData(SenderDecisionStatus.Authorised, "Authorised")]
    [InlineData(SenderDecisionStatus.UnderReview, "Under review")]
    [InlineData(SenderDecisionStatus.Unauthorised, "Unauthorised")]
    public void StatusLabel_ReadsLikeThePage(SenderDecisionStatus? status, string expected) =>
        Assert.Equal(expected, SenderDecisionPresentation.StatusLabel(status));

    [Fact]
    public void Provenance_SaysWhereTheDecisionCameFrom()
    {
        var groupNames = new Dictionary<int, string> { [4] = "Aurora Retail" };
        var fromGroup = new ResolvedSenderDecision(
            SenderDecisionStatus.Authorised, SenderDecisionLevel.Group,
            new SenderDecision { Scope = SenderDecisionScope.Group, GroupId = 4, Kind = SenderDecisionKind.Service, Key = "Mailchimp", DecidedBy = "u", DecidedByName = "Sam Lee" }, null);

        Assert.Equal("set on this domain", SenderDecisionPresentation.Provenance(Decided(SenderDecisionStatus.Authorised), groupNames));
        Assert.Equal("from the Aurora Retail group", SenderDecisionPresentation.Provenance(fromGroup, groupNames));
        Assert.Equal("from the baseline", SenderDecisionPresentation.Provenance(Decided(SenderDecisionStatus.Authorised, SenderDecisionLevel.Baseline), groupNames));
        Assert.Equal(string.Empty, SenderDecisionPresentation.Provenance(Unreviewed, groupNames));
    }
}
