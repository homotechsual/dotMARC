namespace DotMarc.Reporting;

/// <summary>The Dashboard's Group dropdown for a person limited to several groups. Their list normally shows one group at a time, but a link from
/// Baselines spans all of them, so the dropdown offers an explicit "All my groups" choice and shows it as selected in that state.</summary>
public static class DashboardGroupChoice
{
    /// <summary>The dropdown value standing for every group the person may see. No real group has this id.</summary>
    public const int AllMyGroupsId = -1;

    public const string AllMyGroupsText = "All my groups";

    /// <summary>Whether the dropdown offers the choice: only to someone limited to more than one group.</summary>
    public static bool IsOffered(IReadOnlyCollection<int>? scopedGroupIds) => scopedGroupIds is { Count: > 1 };

    /// <summary>What the dropdown shows as selected.</summary>
    public static int? DropdownValue(bool allScopedGroups, int? selectedGroupId, IReadOnlyCollection<int>? scopedGroupIds) =>
        allScopedGroups && IsOffered(scopedGroupIds) ? AllMyGroupsId : selectedGroupId;

    /// <summary>What a dropdown choice means for the list: every scoped group, or one group (or none, for someone with no limit).</summary>
    public static (bool AllScopedGroups, int? GroupId) FromDropdownValue(int? value) =>
        value == AllMyGroupsId ? (true, null) : (false, value);
}
