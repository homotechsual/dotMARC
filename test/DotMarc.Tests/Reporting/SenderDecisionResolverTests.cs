using DotMarc.Data;
using DotMarc.Reporting;
using Xunit;

namespace DotMarc.Tests.Reporting;

public sealed class SenderDecisionResolverTests
{
    private const int DomainId = 7;
    private const int OtherDomainId = 8;
    private const int AuroraGroupId = 1;
    private const int BrightlineGroupId = 2;
    private const int UnrelatedGroupId = 3;

    private static readonly IReadOnlyDictionary<int, string> GroupNames = new Dictionary<int, string>
    {
        [AuroraGroupId] = "Aurora Retail", [BrightlineGroupId] = "Brightline Legal", [UnrelatedGroupId] = "Elsewhere",
    };

    private static SenderDecision OnDomain(int domainId, SenderDecisionStatus status, string key = "Mailchimp", SenderDecisionKind kind = SenderDecisionKind.Service) =>
        new() { Scope = SenderDecisionScope.Domain, DomainId = domainId, Kind = kind, Key = key, Status = status, DecidedBy = "user-1", DecidedByName = "Sam Lee" };

    private static SenderDecision OnGroup(int groupId, SenderDecisionStatus status, string key = "Mailchimp") =>
        new() { Scope = SenderDecisionScope.Group, GroupId = groupId, Kind = SenderDecisionKind.Service, Key = key, Status = status, DecidedBy = "user-1", DecidedByName = "Sam Lee" };

    private static SenderDecision OnBaseline(SenderDecisionStatus status, string key = "Mailchimp") =>
        new() { Scope = SenderDecisionScope.Baseline, Kind = SenderDecisionKind.Service, Key = key, Status = status, DecidedBy = "user-1", DecidedByName = "Sam Lee" };

    private static ResolvedSenderDecision Resolve(IReadOnlyList<SenderDecision> decisions, string? serviceName = "Mailchimp", string ip = "198.51.100.7") =>
        SenderDecisionResolver.Resolve(decisions, DomainId, [AuroraGroupId, BrightlineGroupId], serviceName, ip, GroupNames);

    [Fact]
    public void NoDecisions_IsUnreviewed_AtLevelNone()
    {
        var resolved = Resolve([]);

        Assert.Null(resolved.Status);
        Assert.Equal(SenderDecisionLevel.None, resolved.Level);
        Assert.Null(resolved.Decision);
        Assert.Null(resolved.Conflict);
    }

    [Fact]
    public void ADomainIpDecision_BeatsADomainServiceDecision()
    {
        var resolved = Resolve([
            OnDomain(DomainId, SenderDecisionStatus.Authorised),
            OnDomain(DomainId, SenderDecisionStatus.Unauthorised, "198.51.100.7", SenderDecisionKind.Ip)]);

        Assert.Equal(SenderDecisionStatus.Unauthorised, resolved.Status);
        Assert.Equal(SenderDecisionLevel.Domain, resolved.Level);
        Assert.Equal(SenderDecisionKind.Ip, resolved.Decision!.Kind);
    }

    [Fact]
    public void ADomainServiceDecision_BeatsAGroupDecision_WhichBeatsTheBaseline()
    {
        var withDomain = Resolve([OnBaseline(SenderDecisionStatus.Authorised), OnGroup(AuroraGroupId, SenderDecisionStatus.UnderReview), OnDomain(DomainId, SenderDecisionStatus.Unauthorised)]);
        var withGroup = Resolve([OnBaseline(SenderDecisionStatus.Authorised), OnGroup(AuroraGroupId, SenderDecisionStatus.UnderReview)]);

        Assert.Equal((SenderDecisionStatus.Unauthorised, SenderDecisionLevel.Domain), (withDomain.Status, withDomain.Level));
        Assert.Equal((SenderDecisionStatus.UnderReview, SenderDecisionLevel.Group), (withGroup.Status, withGroup.Level));
    }

    [Fact]
    public void ABaselineDecision_AppliesWhenNothingMoreSpecificExists()
    {
        var resolved = Resolve([OnBaseline(SenderDecisionStatus.Authorised)]);

        Assert.Equal((SenderDecisionStatus.Authorised, SenderDecisionLevel.Baseline), (resolved.Status, resolved.Level));
    }

    [Fact]
    public void AnUnrecognisedSource_WithANullServiceName_OnlyMatchesIpDecisions()
    {
        var serviceOnly = Resolve([OnBaseline(SenderDecisionStatus.Authorised, "Mailchimp"), OnDomain(DomainId, SenderDecisionStatus.Authorised, "Mailchimp")], serviceName: null);
        var withIp = Resolve([OnDomain(DomainId, SenderDecisionStatus.Unauthorised, "198.51.100.7", SenderDecisionKind.Ip)], serviceName: null);

        Assert.Null(serviceOnly.Status);
        Assert.Equal(SenderDecisionStatus.Unauthorised, withIp.Status);
    }

    [Fact]
    public void AnotherDomainsDecision_IsIgnored_AndSoIsAGroupTheDomainIsNotIn()
    {
        var resolved = Resolve([OnDomain(OtherDomainId, SenderDecisionStatus.Unauthorised), OnGroup(UnrelatedGroupId, SenderDecisionStatus.Unauthorised)]);

        Assert.Null(resolved.Status);
    }

    [Theory]
    [InlineData(SenderDecisionStatus.Authorised, SenderDecisionStatus.Unauthorised, SenderDecisionStatus.Unauthorised)]
    [InlineData(SenderDecisionStatus.Authorised, SenderDecisionStatus.UnderReview, SenderDecisionStatus.UnderReview)]
    [InlineData(SenderDecisionStatus.UnderReview, SenderDecisionStatus.Unauthorised, SenderDecisionStatus.Unauthorised)]
    public void ConflictingGroupDecisions_ResolveToTheMostCautious(SenderDecisionStatus aurora, SenderDecisionStatus brightline, SenderDecisionStatus expected)
    {
        var resolved = Resolve([OnGroup(AuroraGroupId, aurora), OnGroup(BrightlineGroupId, brightline)]);

        Assert.Equal(expected, resolved.Status);
        Assert.Equal(SenderDecisionLevel.Group, resolved.Level);
        Assert.Equal($"Aurora Retail says {Label(aurora)}, Brightline Legal says {Label(brightline)}", resolved.Conflict);
    }

    [Fact]
    public void AgreeingGroupDecisions_HaveNoConflict()
    {
        var resolved = Resolve([OnGroup(AuroraGroupId, SenderDecisionStatus.Authorised), OnGroup(BrightlineGroupId, SenderDecisionStatus.Authorised)]);

        Assert.Equal(SenderDecisionStatus.Authorised, resolved.Status);
        Assert.Null(resolved.Conflict);
    }

    [Fact]
    public void ADomainDecision_SettlesAGroupConflict_WithNoConflictText()
    {
        var resolved = Resolve([
            OnGroup(AuroraGroupId, SenderDecisionStatus.Authorised), OnGroup(BrightlineGroupId, SenderDecisionStatus.Unauthorised),
            OnDomain(DomainId, SenderDecisionStatus.Authorised)]);

        Assert.Equal((SenderDecisionStatus.Authorised, SenderDecisionLevel.Domain), (resolved.Status, resolved.Level));
        Assert.Null(resolved.Conflict);
    }

    private static string Label(SenderDecisionStatus status) => status switch
    {
        SenderDecisionStatus.Authorised => "Authorised",
        SenderDecisionStatus.UnderReview => "Under review",
        _ => "Unauthorised",
    };
}
