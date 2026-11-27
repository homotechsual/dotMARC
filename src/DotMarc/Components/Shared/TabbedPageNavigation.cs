namespace DotMarc.Components.Shared;

/// <summary>The address logic <see cref="TabbedPage"/> uses to pick its active tab, kept apart from the component so it can be tested.</summary>
public static class TabbedPageNavigation
{
    /// <summary>The visible tab whose address matches the page, ignoring query, fragment, trailing slash and case. A page that matches no tab
    /// (for example one reached by its group route) shows the first tab as active.</summary>
    public static int ActiveIndex(string baseRelativePath, IReadOnlyList<TabbedPageTab> visibleTabs)
    {
        var path = "/" + baseRelativePath.Split('?', '#')[0].TrimEnd('/');
        for (var index = 0; index < visibleTabs.Count; index++)
        {
            if (string.Equals(visibleTabs[index].Href, path, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 0;
    }

    /// <summary>The first tab a person can see, or none. Menu entries and landing pages send people there.</summary>
    public static TabbedPageTab? FirstVisible(IReadOnlyList<TabbedPageTab> tabs) => tabs.FirstOrDefault(tab => tab.Visible);
}
