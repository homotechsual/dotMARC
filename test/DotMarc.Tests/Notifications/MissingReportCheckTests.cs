using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Reporting;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class MissingReportCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public void DefaultWaitsThreeFullDays(int days, bool expected)
    {
        var domain = Domain(Now.AddDays(-days));
        var rules = AlertRuleSet.Defaults();
        Assert.Equal(3, Assert.Single(rules.For(AlertTypes.MissedReport, DomainFacts.None)).Int(AlertRuleParameterKeys.MissingDays));
        Assert.Equal(expected, MissingReportCheck.IsOverdue(domain, rules, Now));
        Assert.Equal(expected ? "Missing" : "OK", DashboardSummary.StatusOf(domain, 1, rules, Now));
        Assert.Equal(expected ? 1 : 0, DashboardActionCentre.Build([domain], new Dictionary<int, int>(), new(), Now, rules).MissingReports);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public void NeverReportedDomainsGetAnInitialGracePeriod(int age, bool expected)
    {
        var domain = Domain(null);
        domain.FirstSeenUtc = Now.AddDays(-age);
        Assert.Equal(expected, MissingReportCheck.IsOverdue(domain, AlertRuleSet.Defaults(), Now));
    }

    [Fact]
    public void ScopedThresholdsAndAliasExclusionsApplyToDashboardAndSummary()
    {
        var domain = Domain(Now.AddDays(-4));
        domain.Tags = [new Tag { Id = 9, Name = "Alias", Color = MudBlazor.Color.Default }];
        var rules = AlertRuleSeeder.DefaultPack();
        var missed = rules.Single(rule => rule.Kind == AlertTypes.MissedReport);
        missed.Scope = [new() { Target = AlertRuleScopeTarget.Tag, TargetId = 9, Exclude = true }];
        var set = new AlertRuleSet(rules);
        Assert.False(MissingReportCheck.IsOverdue(domain, set, Now));
        Assert.Equal("OK", DashboardSummary.StatusOf(domain, 1, set, Now));
        Assert.Equal(0, DashboardActionCentre.Build([domain], new Dictionary<int, int>(), new(), Now, set).MissingReports);
        missed.Scope.Clear();
        missed.ParametersJson = "{\"days\":\"7\"}";
        Assert.False(MissingReportCheck.IsOverdue(domain, new AlertRuleSet(rules), Now));
        missed.ParametersJson = "{\"days\":\"2\"}";
        Assert.True(MissingReportCheck.IsOverdue(domain, new AlertRuleSet(rules), Now));
        missed.Enabled = false;
        Assert.False(MissingReportCheck.IsOverdue(domain, new AlertRuleSet(rules), Now));
    }

    [Fact]
    public void NullSpfAndUnmonitoredDomainsRemainExempt()
    {
        var domain = Domain(null);
        domain.SpfCheckStatus = SpfCheckStatus.NullSpf;
        Assert.False(MissingReportCheck.IsOverdue(domain, AlertRuleSet.Defaults(), Now));
        domain.SpfCheckStatus = SpfCheckStatus.Ok;
        domain.IsMonitored = false;
        Assert.False(MissingReportCheck.IsOverdue(domain, AlertRuleSet.Defaults(), Now));
    }

    private static Domain Domain(DateTimeOffset? last) => new()
    {
        Id = 1, Name = "example.com", IsMonitored = true, FirstSeenUtc = Now.AddDays(-30), LastReportReceivedUtc = last
    };
}
