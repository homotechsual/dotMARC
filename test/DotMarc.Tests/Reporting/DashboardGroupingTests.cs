using DotMarc.Data;
using DotMarc.Reporting;
using MudBlazor;
using Xunit;

namespace DotMarc.Tests.Reporting;

public sealed class DashboardGroupingTests
{
    private static DashboardDomainRow Row(int id, string name) =>
        new(id, name, "OK", Color.Success, 1, null, true, default, default, default, default, default);

    private static readonly IReadOnlyList<(int Id, string Name)> Groups = [(1, "Contoso"), (2, "Fabrikam"), (3, "Empty")];

    private static Dictionary<int, IReadOnlyList<int>> Membership(params (int Domain, int[] Groups)[] entries) =>
        entries.ToDictionary(entry => entry.Domain, entry => (IReadOnlyList<int>)entry.Groups);

    [Fact]
    public void EachGroupGetsItsOwnTable_InTheOrderGiven_AndGroupsWithNoDomainsAreLeftOut()
    {
        var result = DashboardGrouping.Group(
            [Row(10, "a.com"), Row(11, "b.com"), Row(12, "c.com")],
            Membership((10, [2]), (11, [1]), (12, [1])),
            Groups);

        Assert.Equal(["Contoso", "Fabrikam"], result.Select(group => group.GroupName));
        Assert.Equal(["b.com", "c.com"], result[0].Rows.Select(row => row.Name));
        Assert.Equal(["a.com"], result[1].Rows.Select(row => row.Name));
    }

    [Fact]
    public void ADomainInSeveralGroups_AppearsInEachOfThem()
    {
        var result = DashboardGrouping.Group([Row(10, "a.com")], Membership((10, [1, 2])), Groups);

        Assert.Equal(2, result.Count);
        Assert.All(result, group => Assert.Equal(["a.com"], group.Rows.Select(row => row.Name)));
    }

    [Fact]
    public void DomainsInNoVisibleGroup_GoLast_UnderANullGroup()
    {
        var result = DashboardGrouping.Group(
            [Row(10, "a.com"), Row(11, "loose.com"), Row(12, "elsewhere.com")],
            Membership((10, [1]), (12, [99])),
            Groups);

        Assert.Equal(["Contoso", null], result.Select(group => group.GroupName));
        Assert.Null(result[^1].GroupId);
        Assert.Equal(["loose.com", "elsewhere.com"], result[^1].Rows.Select(row => row.Name));
    }

    [Fact]
    public void NoDomains_GivesNoTables() =>
        Assert.Empty(DashboardGrouping.Group([], Membership(), Groups));
}
