namespace DotMarc.Notifications;

/// <summary>Halo's dropdowns (ticket type, priority, closed status, agent) are saved as numeric ids, and the
/// names only come from asking Halo. Without them the settings page would show a bare number, or nothing, until
/// someone reloads the lists. So the name is saved beside the id, and shown until the lists are loaded.</summary>
public static class HaloOptionNames
{
    /// <summary>Copies the names of the selected options onto <paramref name="settings"/>, taking each from
    /// the lists loaded from Halo. A selection missing from a list keeps the name already saved: the lists
    /// have to be loaded to change a selection, so an id that isn't in one hasn't changed.</summary>
    public static void Remember(
        HaloPsaSettings settings,
        IEnumerable<HaloTicketType> ticketTypes,
        IEnumerable<HaloPriority> priorities,
        IEnumerable<HaloTicketStatus> statuses,
        IEnumerable<HaloAgent> agents)
    {
        settings.TicketTypeName = NameFor(ticketTypes.Select(option => (option.Id, option.Name)), settings.TicketTypeId, settings.TicketTypeName);
        settings.DefaultPriorityName = NameFor(priorities.Select(option => (option.Id, option.Name)), settings.DefaultPriorityId, settings.DefaultPriorityName);
        settings.ClosedStatusName = NameFor(statuses.Select(option => (option.Id, option.Name)), settings.ClosedStatusId, settings.ClosedStatusName);
        settings.AssignedAgentName = NameFor(agents.Select(option => (option.Id, option.Name)), settings.AssignedAgentId, settings.AssignedAgentName);
    }

    /// <summary>The options a dropdown should offer: everything loaded from Halo, plus the saved selection
    /// (by its saved name) when the loaded lists don't contain it, so it still shows by name.</summary>
    public static IReadOnlyList<(int Id, string Name)> WithSaved(IEnumerable<(int Id, string Name)> loaded, int? savedId, string? savedName)
    {
        var options = loaded.ToList();
        if (savedId is { } id && !string.IsNullOrWhiteSpace(savedName) && options.All(option => option.Id != id))
        {
            options.Insert(0, (id, savedName));
        }

        return options;
    }

    private static string? NameFor(IEnumerable<(int Id, string Name)> loaded, int? selectedId, string? savedName)
    {
        if (selectedId is not { } id)
        {
            return null;
        }

        foreach (var option in loaded)
        {
            if (option.Id == id)
            {
                return option.Name;
            }
        }

        return savedName;
    }
}
