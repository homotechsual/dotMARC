namespace DotMarc.Reporting;

/// <summary>One group's table on the Dashboard's grouped view. <see cref="GroupName"/> is null for the domains in no
/// visible group.</summary>
public sealed record DashboardDomainGroup(int? GroupId, string? GroupName, IReadOnlyList<DashboardDomainRow> Rows);

public static class DashboardGrouping
{
    /// <summary>Splits <paramref name="rows"/> into one table per group, groups in the order given and the domains in no
    /// group last. A domain in several groups appears in each of them. Groups with no domains are left out, and only
    /// <paramref name="visibleGroups"/> count, so a person limited to some groups never sees another's name.</summary>
    public static IReadOnlyList<DashboardDomainGroup> Group(
        IReadOnlyList<DashboardDomainRow> rows,
        IReadOnlyDictionary<int, IReadOnlyList<int>> groupIdsByDomainId,
        IReadOnlyList<(int Id, string Name)> visibleGroups)
    {
        var visibleIds = visibleGroups.Select(group => group.Id).ToHashSet();
        var groups = new List<DashboardDomainGroup>();

        foreach (var (id, name) in visibleGroups)
        {
            var members = rows.Where(row => groupIdsByDomainId.TryGetValue(row.Id, out var ids) && ids.Contains(id)).ToList();
            if (members.Count > 0)
            {
                groups.Add(new DashboardDomainGroup(id, name, members));
            }
        }

        var ungrouped = rows
            .Where(row => !groupIdsByDomainId.TryGetValue(row.Id, out var ids) || !ids.Any(visibleIds.Contains))
            .ToList();
        if (ungrouped.Count > 0)
        {
            groups.Add(new DashboardDomainGroup(null, null, ungrouped));
        }

        return groups;
    }
}
