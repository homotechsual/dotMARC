using DotMarc.Baselines;
using DotMarc.Data;
using DotMarc.Reporting;
using Xunit;

namespace DotMarc.Tests.Reporting;

public sealed class DomainListStateTests
{
    [Fact]
    public void DashboardAttentionViewRoundTripsForBack_ButIsNotADomainFilterOrSavedDefault()
    {
        var state = new DomainListState { Search = "alpha", AttentionView = "senders" };
        var restored = DomainListState.FromQuery(new Uri("https://dotmarc.example" + state.ToUrl("/dashboard")));
        Assert.Equal(state, restored);
        Assert.Single(state.ActiveFilters);
        Assert.Null(state.AsDefault().AttentionView);
        Assert.DoesNotContain("attention", state.ToUrl("/domains"));
        Assert.Throws<FormatException>(() => DomainListState.FromQuery(new Uri("https://dotmarc.example/domains?attention=senders")));
        Assert.Throws<FormatException>(() => (state with { AttentionView = "unknown" }).Validate());
    }

    [Fact]
    public void FiltersAreAppliedBeforeGrouping_AndCountsAreDistinct()
    {
        var rows = new[] { Row(1, "alpha.example", 0.99), Row(2, "beta.example", 0.8) };
        var state = new DomainListState { MinPassRatePercent = 95 };
        var filtered = rows.Where(row => state.Matches(row, [1, 2], [3], 10, [])).ToList();
        var grouped = DashboardGrouping.Group(filtered, new Dictionary<int, IReadOnlyList<int>> { [1] = [1, 2], [2] = [3] },
            [(1, "A"), (2, "B"), (3, "C")]);

        Assert.Single(filtered);
        Assert.Equal(2, grouped.Count);
        Assert.All(grouped, group => Assert.Equal(1, Assert.Single(group.Rows).Id));
        Assert.Single(state.ActiveFilters);
    }

    [Fact]
    public void TargetFilterRequiresMonitoredDomain_AndComposesWithOtherCriteria()
    {
        var state = new DomainListState { Target = DmarcTargetOutcome.Below, TagId = 3 };
        var row = Row(1, "alpha.example", null) with { TargetOutcome = DmarcTargetOutcome.Below };
        Assert.True(state.Matches(row, [], [3], 0, []));
        Assert.False(state.Matches(row with { IsMonitored = false }, [], [3], 0, []));
        Assert.False(state.Matches(row, [], [4], 0, []));
        Assert.False((state with { MinPassRatePercent = 1 }).Matches(row, [], [3], 0, []));
    }

    [Fact]
    public void DefaultsExcludeFreeText_ButRetainTargetScopeAndSort()
    {
        var state = new DomainListState { Search = "alpha", LastReport = "never", AllMyGroups = true, Target = DmarcTargetOutcome.Below, Sort = "pass", Descending = true };
        var defaults = state.AsDefault();
        Assert.Null(defaults.Search);
        Assert.Null(defaults.LastReport);
        Assert.True(defaults.AllMyGroups);
        Assert.Equal(state.Target, defaults.Target);
        Assert.Equal("pass", defaults.Sort);
        Assert.True(defaults.Descending);
    }

    [Fact]
    public void QueryRoundTrip_IsExplicitAndRejectsBadValues()
    {
        var state = new DomainListState { TagId = 2, Target = DmarcTargetOutcome.Below, GroupedView = true, AllMyGroups = true, Search = "a & b" };
        Assert.Equal(state, DomainListState.FromQuery(new Uri("https://example.test" + state.ToUrl("/domains"))));
        Assert.Throws<FormatException>(() => DomainListState.FromQuery(new Uri("https://example.test/domains?minPass=101")));
        Assert.Throws<FormatException>(() => DomainListState.FromQuery(new Uri("https://example.test/domains?target=bad")));
        Assert.Throws<FormatException>(() => DomainListState.FromQuery(new Uri("https://example.test/domains?group=-50")));
    }

    [Fact]
    public void SortHasDeterministicTies_AndIsIndependentOfGrouping()
    {
        var rows = new[] { Row(2, "same.example", 1), Row(1, "same.example", 1) };
        Assert.Equal([1, 2], new DomainListState { Sort = "pass" }.Order(rows).Select(row => row.Id));
    }

    [Theory]
    [InlineData("?minPass=NaN")]
    [InlineData("?minReports=-1")]
    [InlineData("?sort=arbitrary")]
    [InlineData("?dmarc=arbitrary")]
    [InlineData("?dkim=arbitrary")]
    [InlineData("?mta=arbitrary")]
    [InlineData("?tag=1&tag=2")]
    [InlineData("?unrecognised=1")]
    public void InvalidQueryCriteriaAreRejected(string query) =>
        Assert.Throws<FormatException>(() => DomainListState.FromQuery(new Uri("https://example.test/domains" + query)));

    [Fact]
    public void LegacyDefaultsSeedBothPages_AndFutureVersionsAreNotOverwritten()
    {
        var defaults = DomainListDefaults.Parse("""{"groupedView":true,"groupId":4,"tagId":2,"minPassRatePercent":90}""");
        Assert.True(defaults.Inventory!.GroupedView);
        Assert.Equal(4, defaults.Dashboard!.GroupId);
        Assert.False(defaults.Dashboard.GroupedView);
        Assert.Throws<FormatException>(() => DomainListDefaults.Parse("""{"version":99}"""));
    }

    private static DashboardDomainRow Row(int id, string name, double? passRate) =>
        new(id, name, "OK", MudBlazor.Color.Success, passRate, null, true, default, default, default, default, default);
}
