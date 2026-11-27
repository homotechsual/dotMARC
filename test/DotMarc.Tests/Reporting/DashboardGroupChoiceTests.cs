using DotMarc.Reporting;
using Xunit;

namespace DotMarc.Tests.Reporting;

/// <summary>The Dashboard's "All my groups" choice, so a group-limited person arriving from a target link sees what the list shows.</summary>
public sealed class DashboardGroupChoiceTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(new int[0], false)]
    [InlineData(new[] { 3 }, false)]
    [InlineData(new[] { 3, 4 }, true)]
    public void TheChoice_IsOfferedOnlyToSomeoneLimitedToSeveralGroups(int[]? scopedGroupIds, bool expectedOffered) =>
        Assert.Equal(expectedOffered, DashboardGroupChoice.IsOffered(scopedGroupIds));

    [Fact]
    public void AfterATargetLink_TheDropdownShowsAllMyGroups_ForALimitedPerson() =>
        Assert.Equal(DashboardGroupChoice.AllMyGroupsId, DashboardGroupChoice.DropdownValue(allScopedGroups: true, selectedGroupId: null, [3, 4]));

    [Fact]
    public void WithOneGroupChosen_TheDropdownShowsThatGroup() =>
        Assert.Equal(4, DashboardGroupChoice.DropdownValue(allScopedGroups: false, selectedGroupId: 4, [3, 4]));

    [Fact]
    public void ForSomeoneWithNoLimit_TheDropdownNeverShowsAllMyGroups()
    {
        Assert.Null(DashboardGroupChoice.DropdownValue(allScopedGroups: true, selectedGroupId: null, scopedGroupIds: null));
        Assert.Equal(5, DashboardGroupChoice.DropdownValue(allScopedGroups: false, selectedGroupId: 5, scopedGroupIds: null));
    }

    [Fact]
    public void ChoosingAllMyGroups_WidensTheListToTheirGroups_AndChoosingAGroupNarrowsIt()
    {
        Assert.Equal((true, (int?)null), DashboardGroupChoice.FromDropdownValue(DashboardGroupChoice.AllMyGroupsId));
        Assert.Equal((false, (int?)4), DashboardGroupChoice.FromDropdownValue(4));
        Assert.Equal((false, (int?)null), DashboardGroupChoice.FromDropdownValue(null));
    }
}
