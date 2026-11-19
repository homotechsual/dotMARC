using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class AlertRulePresentationTests
{
    private static readonly AlertScopeNames Names = new(
        Groups: new Dictionary<int, string> { [1] = "Aurora Retail", [2] = "Cobalt Freight" },
        Tags: new Dictionary<int, string> { [5] = "Finance" },
        Domains: new Dictionary<int, string> { [9] = "shop.aurora-retail.example", [10] = "mail.cobalt.example" });

    private static AlertRule Rule(int id, string name = "Rule", int position = 0, bool enabled = true, bool stop = true, params AlertRuleScopeEntry[] scope) => new()
    {
        Id = id, Kind = AlertTypes.MissedReport, Name = name, Severity = "Warning", Position = position, Enabled = enabled, StopProcessing = stop, Scope = [.. scope]
    };

    private static AlertRuleScopeEntry Include(AlertRuleScopeTarget target, int id) => new() { Target = target, TargetId = id };
    private static AlertRuleScopeEntry Exclude(AlertRuleScopeTarget target, int id) => new() { Target = target, TargetId = id, Exclude = true };

    private static DomainFacts Domain(int id, int[]? groups = null, int[]? tags = null) => new(id, (groups ?? []).ToHashSet(), (tags ?? []).ToHashSet());

    // ---- DescribeScope ----

    [Fact]
    public void AllDomains_WhenThereIsNoInclude()
    {
        Assert.Equal("All domains", AlertRulePresentation.DescribeScope(Rule(1), Names, hasDomainScope: true));
    }

    [Fact]
    public void IncludesAreListedByKind_UsingTheirNames()
    {
        var rule = Rule(1, scope: [Include(AlertRuleScopeTarget.Group, 1), Include(AlertRuleScopeTarget.Tag, 5)]);

        Assert.Equal("Group Aurora Retail and tag Finance", AlertRulePresentation.DescribeScope(rule, Names, hasDomainScope: true));
    }

    [Fact]
    public void SeveralOfOneKind_AreJoinedWithAnd()
    {
        var rule = Rule(1, scope: [Include(AlertRuleScopeTarget.Group, 1), Include(AlertRuleScopeTarget.Group, 2)]);

        Assert.Equal("Groups Aurora Retail and Cobalt Freight", AlertRulePresentation.DescribeScope(rule, Names, hasDomainScope: true));
    }

    [Fact]
    public void ThreeKindsOfInclude_AreCommaSeparatedWithAndBeforeTheLast()
    {
        var rule = Rule(1, scope: [Include(AlertRuleScopeTarget.Group, 1), Include(AlertRuleScopeTarget.Tag, 5), Include(AlertRuleScopeTarget.Domain, 10)]);

        Assert.Equal("Group Aurora Retail, tag Finance and mail.cobalt.example", AlertRulePresentation.DescribeScope(rule, Names, hasDomainScope: true));
    }

    [Fact]
    public void ExcludesAreAppended_AndWorkWithoutAnyInclude()
    {
        var onlyExclude = Rule(1, scope: [Exclude(AlertRuleScopeTarget.Domain, 9)]);
        var both = Rule(2, scope: [Include(AlertRuleScopeTarget.Group, 1), Exclude(AlertRuleScopeTarget.Domain, 9), Exclude(AlertRuleScopeTarget.Tag, 5)]);

        Assert.Equal("All domains except shop.aurora-retail.example", AlertRulePresentation.DescribeScope(onlyExclude, Names, hasDomainScope: true));
        Assert.Equal("Group Aurora Retail except shop.aurora-retail.example and tag Finance", AlertRulePresentation.DescribeScope(both, Names, hasDomainScope: true));
    }

    [Fact]
    public void ASystemKind_SaysItIsNotAboutADomain()
    {
        Assert.Equal("Not about a domain", AlertRulePresentation.DescribeScope(Rule(1), Names, hasDomainScope: false));
    }

    [Fact]
    public void ADeletedTarget_IsNamedAsGone()
    {
        var oneGone = Rule(1, scope: [Include(AlertRuleScopeTarget.Group, 1), Include(AlertRuleScopeTarget.Group, 404)]);
        var twoGone = Rule(2, scope: [Include(AlertRuleScopeTarget.Group, 404), Include(AlertRuleScopeTarget.Group, 405)]);

        Assert.Equal("Group Aurora Retail and a group that no longer exists", AlertRulePresentation.DescribeScope(oneGone, Names, hasDomainScope: true));
        Assert.Equal("2 groups that no longer exist", AlertRulePresentation.DescribeScope(twoGone, Names, hasDomainScope: true));
    }

    [Theory]
    [InlineData(AlertRuleScopeTarget.Group, 1, false)]
    [InlineData(AlertRuleScopeTarget.Group, 3, true)]
    [InlineData(AlertRuleScopeTarget.Tag, 5, false)]
    [InlineData(AlertRuleScopeTarget.Tag, 6, true)]
    [InlineData(AlertRuleScopeTarget.Domain, 9, false)]
    [InlineData(AlertRuleScopeTarget.Domain, 99, true)]
    public void IsDeadTarget_IsTrueOnlyForAMissingId(AlertRuleScopeTarget target, int id, bool dead)
    {
        Assert.Equal(dead, AlertRulePresentation.IsDeadTarget(Include(target, id), Names));
    }

    // ---- Reach ----

    [Fact]
    public void Reach_CountsDomainsAndHonoursStop()
    {
        var aurora = Rule(1, "Aurora", position: -10, scope: Include(AlertRuleScopeTarget.Group, 1));
        var everything = Rule(2, "Default", position: 0);
        var domains = new[] { Domain(1, groups: [1]), Domain(2, groups: [1]), Domain(3), Domain(4) };

        var reach = AlertRulePresentation.Reach([everything, aurora], domains);

        var auroraReach = reach.Single(entry => entry.RuleId == 1);
        Assert.Equal((2, 0), (auroraReach.AppliesTo, auroraReach.Hidden));
        Assert.Empty(auroraReach.HiddenByRuleNames);
        var defaultReach = reach.Single(entry => entry.RuleId == 2);
        Assert.Equal((2, 2), (defaultReach.AppliesTo, defaultReach.Hidden));
        Assert.Equal(["Aurora"], defaultReach.HiddenByRuleNames);
    }

    [Fact]
    public void Reach_ADisabledRule_AppliesToNone_AndHidesNothing()
    {
        var disabled = Rule(1, "Off", position: -10, enabled: false);
        var everything = Rule(2, "Default", position: 0);

        var reach = AlertRulePresentation.Reach([disabled, everything], [Domain(1), Domain(2)]);

        var offReach = reach.Single(entry => entry.RuleId == 1);
        Assert.Equal((0, 0), (offReach.AppliesTo, offReach.Hidden));
        Assert.Empty(offReach.HiddenByRuleNames);
        Assert.Equal((2, 0), (reach.Single(entry => entry.RuleId == 2).AppliesTo, reach.Single(entry => entry.RuleId == 2).Hidden));
    }

    [Fact]
    public void Reach_ARuleThatDoesNotStop_HidesNothing()
    {
        var extra = Rule(1, "Extra", position: -10, stop: false);
        var everything = Rule(2, "Default", position: 0);

        var reach = AlertRulePresentation.Reach([extra, everything], [Domain(1), Domain(2), Domain(3)]);

        Assert.All(reach, entry => Assert.Equal((3, 0), (entry.AppliesTo, entry.Hidden)));
    }

    [Fact]
    public void Reach_ExcludesAreRespected()
    {
        var rule = Rule(1, scope: Exclude(AlertRuleScopeTarget.Domain, 2));

        var reach = Assert.Single(AlertRulePresentation.Reach([rule], [Domain(1), Domain(2), Domain(3)]));

        Assert.Equal(2, reach.AppliesTo);
    }

    [Fact]
    public void Reach_OrdersByPositionThenId_WhateverOrderItIsGiven()
    {
        var late = Rule(5, "Late", position: 20);
        var early = Rule(6, "Early", position: 10);

        var reach = AlertRulePresentation.Reach([late, early], [Domain(1)]);

        Assert.Equal([6, 5], reach.Select(entry => entry.RuleId));
        Assert.Equal((1, 0), (reach[0].AppliesTo, reach[0].Hidden));
        Assert.Equal((0, 1), (reach[1].AppliesTo, reach[1].Hidden));
    }

    [Fact]
    public void Reach_NamesEveryRuleThatHidesAnother_Once()
    {
        var first = Rule(1, "First", position: 0, scope: Include(AlertRuleScopeTarget.Group, 1));
        var second = Rule(2, "Second", position: 10, scope: Include(AlertRuleScopeTarget.Group, 2));
        var everything = Rule(3, "Default", position: 20);

        var reach = AlertRulePresentation.Reach([first, second, everything], [Domain(1, groups: [1]), Domain(2, groups: [1]), Domain(3, groups: [2])]);

        var defaultReach = reach.Single(entry => entry.RuleId == 3);
        Assert.Equal(3, defaultReach.Hidden);
        Assert.Equal(["First", "Second"], defaultReach.HiddenByRuleNames);
    }
}
