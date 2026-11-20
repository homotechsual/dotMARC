namespace DotMarc.Notifications;

/// <summary>Works out which destinations an alert goes to. Pure, so every case is a plain unit test.</summary>
public static class AlertDestinationRouting
{
    /// <param name="rule">The rule that raised the alert, with its <see cref="AlertRule.Destinations"/> loaded. Null (an alert
    /// whose rule was deleted, or one raised without a rule) sends to every destination.</param>
    /// <param name="all">Every destination.</param>
    /// <returns>The destinations that are switched on and have a URL. A rule in Selected mode only gets the ones it chose, and a
    /// chosen destination that is switched off is skipped, not replaced by the others.</returns>
    public static IReadOnlyList<AlertDestination> Resolve(AlertRule? rule, IReadOnlyCollection<AlertDestination> all)
    {
        var usable = all.Where(destination => destination.Enabled && !string.IsNullOrWhiteSpace(destination.Url));
        if (rule is null || rule.DestinationMode == AlertDestinationMode.All)
        {
            return [.. usable];
        }

        var chosen = rule.Destinations.Select(link => link.AlertDestinationId).ToHashSet();
        return [.. usable.Where(destination => chosen.Contains(destination.Id))];
    }

    /// <summary>Where a rule's escalation goes: only the destinations ticked for escalation, and only those that are on and have a URL.
    /// A rule with none ticked escalates to nobody.</summary>
    public static IReadOnlyList<AlertDestination> ResolveEscalation(AlertRule rule, IReadOnlyCollection<AlertDestination> all)
    {
        var chosen = rule.EscalationDestinations.Select(link => link.AlertDestinationId).ToHashSet();
        return [.. all.Where(destination => destination.Enabled && !string.IsNullOrWhiteSpace(destination.Url) && chosen.Contains(destination.Id))];
    }
}
