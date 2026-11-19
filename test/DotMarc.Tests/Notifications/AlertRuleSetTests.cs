using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class AlertRuleSetTests
{
    private static readonly NotificationSettings Settings = new();

    private static DomainFacts Facts(int domainId = 1, int[]? groups = null, int[]? tags = null) =>
        new(domainId, (groups ?? []).ToHashSet(), (tags ?? []).ToHashSet());

    private static AlertRule Rule(int id, int position, string kind = AlertTypes.MissedReport, bool enabled = true, bool stop = true, params AlertRuleScopeEntry[] scope) => new()
    {
        Id = id, Kind = kind, Name = $"Rule {id}", Severity = "Warning", Position = position, Enabled = enabled, StopProcessing = stop, Scope = [.. scope]
    };

    private static AlertRuleScopeEntry Include(AlertRuleScopeTarget target, int targetId) => new() { Target = target, TargetId = targetId };
    private static AlertRuleScopeEntry Exclude(AlertRuleScopeTarget target, int targetId) => new() { Target = target, TargetId = targetId, Exclude = true };

    private static IReadOnlyList<int> Ids(IReadOnlyList<ResolvedAlertRule> resolved) => resolved.Select(rule => rule.RuleId).ToList();

    [Fact]
    public void For_ReturnsTheDefaultRule_ForAnyDomain()
    {
        var rules = AlertRuleSet.Defaults(Settings);

        var resolved = rules.For(AlertTypes.MissedReport, Facts(domainId: 77, groups: [4], tags: [9]));

        var only = Assert.Single(resolved);
        Assert.Equal(AlertTypes.MissedReport, only.Rule.Kind);
    }

    [Fact]
    public void For_SkipsADisabledRule()
    {
        var rules = new AlertRuleSet([Rule(1, 10, enabled: false), Rule(2, 20)], Settings);

        Assert.Equal([2], Ids(rules.For(AlertTypes.MissedReport, Facts())));
    }

    [Fact]
    public void For_SkipsARuleWhoseLegacySwitchIsOff()
    {
        var settings = new NotificationSettings { NewSenderAlertEnabled = false };
        var rules = new AlertRuleSet([Rule(1, 10, AlertTypes.NewUnreviewedSender), Rule(2, 20, AlertTypes.NewUnreviewedSender, stop: false)], settings);

        Assert.Empty(rules.For(AlertTypes.NewUnreviewedSender, Facts()));
    }

    [Theory]
    [InlineData(AlertRuleScopeTarget.Group, 4, true)]
    [InlineData(AlertRuleScopeTarget.Group, 5, false)]
    [InlineData(AlertRuleScopeTarget.Tag, 9, true)]
    [InlineData(AlertRuleScopeTarget.Tag, 8, false)]
    [InlineData(AlertRuleScopeTarget.Domain, 1, true)]
    [InlineData(AlertRuleScopeTarget.Domain, 2, false)]
    public void For_Include_MatchesByGroupTagOrDomain(AlertRuleScopeTarget target, int targetId, bool matches)
    {
        var rules = new AlertRuleSet([Rule(1, 10, scope: Include(target, targetId))], Settings);

        var resolved = rules.For(AlertTypes.MissedReport, Facts(domainId: 1, groups: [4], tags: [9]));

        Assert.Equal(matches, resolved.Count == 1);
    }

    [Fact]
    public void For_AnyOneIncludeEntry_IsEnough()
    {
        var rules = new AlertRuleSet([Rule(1, 10, scope: [Include(AlertRuleScopeTarget.Group, 99), Include(AlertRuleScopeTarget.Tag, 9)])], Settings);

        Assert.Single(rules.For(AlertTypes.MissedReport, Facts(tags: [9])));
    }

    [Fact]
    public void For_Exclude_RemovesADomainFromAnAllDomainsRule()
    {
        var rules = new AlertRuleSet([Rule(1, 10, scope: Exclude(AlertRuleScopeTarget.Domain, 1))], Settings);

        Assert.Empty(rules.For(AlertTypes.MissedReport, Facts(domainId: 1)));
        Assert.Single(rules.For(AlertTypes.MissedReport, Facts(domainId: 2)));
    }

    [Fact]
    public void For_Exclude_OverridesAnInclude()
    {
        var rules = new AlertRuleSet([Rule(1, 10, scope: [Include(AlertRuleScopeTarget.Group, 4), Exclude(AlertRuleScopeTarget.Domain, 1)])], Settings);

        Assert.Empty(rules.For(AlertTypes.MissedReport, Facts(domainId: 1, groups: [4])));
        Assert.Single(rules.For(AlertTypes.MissedReport, Facts(domainId: 2, groups: [4])));
    }

    [Fact]
    public void For_StopsAfterARuleThatStops()
    {
        var rules = new AlertRuleSet([Rule(1, 10), Rule(2, 20), Rule(3, 30)], Settings);

        Assert.Equal([1], Ids(rules.For(AlertTypes.MissedReport, Facts())));
    }

    [Fact]
    public void For_ContinuesPastARuleThatDoesNotStop()
    {
        var rules = new AlertRuleSet([Rule(1, 10, stop: false), Rule(2, 20), Rule(3, 30)], Settings);

        Assert.Equal([1, 2], Ids(rules.For(AlertTypes.MissedReport, Facts())));
    }

    [Fact]
    public void For_ADisabledRuleNeverStopsProcessing()
    {
        var rules = new AlertRuleSet([Rule(1, 10, enabled: false), Rule(2, 20)], Settings);

        Assert.Equal([2], Ids(rules.For(AlertTypes.MissedReport, Facts())));
    }

    [Fact]
    public void For_OrdersByPosition_ThenId()
    {
        var rules = new AlertRuleSet([Rule(3, 20, stop: false), Rule(2, 20, stop: false), Rule(1, 30)], Settings);

        Assert.Equal([2, 3, 1], Ids(rules.For(AlertTypes.MissedReport, Facts())));
    }

    [Fact]
    public void For_OnlyReturnsRulesOfTheKind()
    {
        var rules = new AlertRuleSet([Rule(1, 10, AlertTypes.MissedReport), Rule(2, 10, AlertTypes.TlsrptFailure)], Settings);

        Assert.Equal([2], Ids(rules.For(AlertTypes.TlsrptFailure, Facts())));
    }

    [Fact]
    public void For_ASystemKind_IgnoresScope_AndNullFacts()
    {
        var rules = new AlertRuleSet([Rule(1, 10, AlertTypes.ApiKeyExpiring, scope: Include(AlertRuleScopeTarget.Group, 4))], Settings);

        Assert.Single(rules.For(AlertTypes.ApiKeyExpiring, null));
    }

    [Fact]
    public void For_ReturnsNothing_WhenEveryRuleIsOutOfScope()
    {
        var rules = new AlertRuleSet([Rule(1, 10, scope: Include(AlertRuleScopeTarget.Group, 4))], Settings);

        Assert.Empty(rules.For(AlertTypes.MissedReport, Facts(groups: [5])));
    }

    [Fact]
    public void For_ADomainKind_WithNoFacts_MatchesOnlyAnAllDomainsRule()
    {
        var rules = new AlertRuleSet([Rule(1, 10, scope: Include(AlertRuleScopeTarget.Group, 4)), Rule(2, 20)], Settings);

        Assert.Equal([2], Ids(rules.For(AlertTypes.MissedReport, null)));
    }

    [Fact]
    public void Parameters_AreReadFromTheRule()
    {
        var rule = Rule(1, 10);
        rule.ParametersJson = "{\"days\":\"5\"}";
        var rules = new AlertRuleSet([rule], Settings);

        var resolved = Assert.Single(rules.For(AlertTypes.MissedReport, Facts()));

        Assert.Equal(5, resolved.Parameters.Int(AlertRuleParameterKeys.MissingDays, fallback: 2));
    }

    [Fact]
    public void Find_ReturnsARuleById()
    {
        var rules = new AlertRuleSet([Rule(1, 10), Rule(2, 20)], Settings);

        Assert.Equal(2, rules.Find(2)!.Id);
        Assert.Null(rules.Find(99));
    }
}
