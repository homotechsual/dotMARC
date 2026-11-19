namespace DotMarc.Notifications;

/// <summary>The names of the groups, tags and domains a rule's scope can point at, by id. An id that is missing from its dictionary
/// is a target that no longer exists.</summary>
public sealed record AlertScopeNames(
    IReadOnlyDictionary<int, string> Groups,
    IReadOnlyDictionary<int, string> Tags,
    IReadOnlyDictionary<int, string> Domains);

/// <summary>How far a rule reaches: <see cref="AppliesTo"/> domains it applies to, and <see cref="Hidden"/> it would apply to but an
/// earlier rule that stops processing takes first, with the names of those rules.</summary>
public sealed record AlertRuleReach(int RuleId, int AppliesTo, int Hidden, IReadOnlyList<string> HiddenByRuleNames);

/// <summary>Plain-words descriptions of a rule, and what the editor shows about its effect. Pure: no database, no clock.</summary>
public static class AlertRulePresentation
{
    /// <summary>"All domains", "Group Aurora Retail and tag Finance", "All domains except shop.example". A target that has been
    /// deleted reads as "a group that no longer exists". A kind that is not about a domain says so.</summary>
    public static string DescribeScope(AlertRule rule, AlertScopeNames names, bool hasDomainScope)
    {
        if (!hasDomainScope)
        {
            return "Not about a domain";
        }

        var includes = rule.Scope.Where(entry => !entry.Exclude).ToList();
        var excludes = rule.Scope.Where(entry => entry.Exclude).ToList();
        var text = includes.Count == 0 ? "All domains" : Capitalise(Natural(Parts(includes, names)));
        return excludes.Count == 0 ? text : $"{text} except {Natural(Parts(excludes, names))}";
    }

    /// <summary>For the rules of one kind, how many of the given domains each applies to, and how many each is hidden from by an
    /// earlier rule that stops processing. Rules are taken in processing order whatever order they are given in, and a disabled rule
    /// applies to nothing and hides nothing. Results come back in processing order.</summary>
    public static IReadOnlyList<AlertRuleReach> Reach(IReadOnlyList<AlertRule> rulesOfKind, IReadOnlyList<DomainFacts> domains)
    {
        var ordered = rulesOfKind.OrderBy(rule => rule.Position).ThenBy(rule => rule.Id).ToList();
        var appliesTo = ordered.ToDictionary(rule => rule.Id, _ => 0);
        var hidden = ordered.ToDictionary(rule => rule.Id, _ => 0);
        var hiddenBy = ordered.ToDictionary(rule => rule.Id, _ => new List<string>());

        foreach (var domain in domains)
        {
            AlertRule? stoppedBy = null;
            foreach (var rule in ordered.Where(rule => rule.Enabled && AlertRuleScopeMatcher.Matches(rule, domain)))
            {
                if (stoppedBy is not null)
                {
                    hidden[rule.Id]++;
                    if (!hiddenBy[rule.Id].Contains(stoppedBy.Name))
                    {
                        hiddenBy[rule.Id].Add(stoppedBy.Name);
                    }

                    continue;
                }

                appliesTo[rule.Id]++;
                if (rule.StopProcessing)
                {
                    stoppedBy = rule;
                }
            }
        }

        return ordered.Select(rule => new AlertRuleReach(rule.Id, appliesTo[rule.Id], hidden[rule.Id], hiddenBy[rule.Id])).ToList();
    }

    /// <summary>Where a rule's alerts go and whether it opens tickets, in words, or null when it does what every rule does by default
    /// (sends to all destinations and follows the ticket rules). A destination that has since been deleted is left out.</summary>
    public static string? DescribeDelivery(AlertRule rule, IReadOnlyDictionary<int, string> destinationNames)
    {
        var sentences = new List<string>();
        if (rule.DestinationMode == AlertDestinationMode.Selected)
        {
            var names = rule.Destinations.Select(link => destinationNames.GetValueOrDefault(link.AlertDestinationId)).OfType<string>().ToList();
            sentences.Add(names.Count == 0 ? "Sends nowhere." : $"Sends to {Join(names)}.");
        }

        if (rule.TicketMode != AlertTicketMode.FollowRules)
        {
            sentences.Add(rule.TicketMode == AlertTicketMode.Always ? "Always opens a ticket." : "Never opens a ticket.");
        }

        return sentences.Count == 0 ? null : string.Join(" ", sentences);
    }

    private static string Join(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

    /// <summary>A stored rule as an editable input: the same values, with every parameter the kind defines present (the kind's default
    /// for one the rule leaves out), so the editor and a toggle both start from a complete, valid input.</summary>
    public static AlertRuleInput ToInput(AlertRule rule)
    {
        var stored = string.IsNullOrWhiteSpace(rule.ParametersJson)
            ? []
            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(rule.ParametersJson) ?? [];
        var parameters = (AlertRuleKinds.Find(rule.Kind)?.Parameters ?? []).ToDictionary(definition => definition.Key, definition => stored.GetValueOrDefault(definition.Key, definition.Default));
        return new AlertRuleInput(
            rule.Name, rule.Enabled, rule.Severity, rule.StopProcessing, rule.CooldownMinutes, rule.AutoCloseDays, parameters,
            [.. rule.Scope.Select(entry => new AlertRuleScopeInput(entry.Target, entry.TargetId, entry.Exclude))],
            rule.DestinationMode, [.. rule.Destinations.Select(link => link.AlertDestinationId)], rule.TicketMode);
    }

    public static bool IsDeadTarget(AlertRuleScopeEntry entry, AlertScopeNames names) => entry.Target switch
    {
        AlertRuleScopeTarget.Group => !names.Groups.ContainsKey(entry.TargetId),
        AlertRuleScopeTarget.Tag => !names.Tags.ContainsKey(entry.TargetId),
        _ => !names.Domains.ContainsKey(entry.TargetId)
    };

    /// <summary>One part per kind of target, in the order each first appears: "group A", "groups A and B", "tag T", bare domain names,
    /// and for ones that no longer exist "a group that no longer exists" or "2 groups that no longer exist".</summary>
    private static List<string> Parts(IReadOnlyList<AlertRuleScopeEntry> entries, AlertScopeNames names)
    {
        var parts = new List<string>();
        foreach (var target in entries.Select(entry => entry.Target).Distinct())
        {
            var ofTarget = entries.Where(entry => entry.Target == target).ToList();
            var live = ofTarget.Where(entry => !IsDeadTarget(entry, names)).Select(entry => NameOf(entry, names)).ToList();
            var deadCount = ofTarget.Count - live.Count;
            var label = target.ToString().ToLowerInvariant();

            if (target == AlertRuleScopeTarget.Domain)
            {
                parts.AddRange(live);
            }
            else if (live.Count == 1)
            {
                parts.Add($"{label} {live[0]}");
            }
            else if (live.Count > 1)
            {
                parts.Add($"{label}s {Natural(live)}");
            }

            if (deadCount == 1)
            {
                parts.Add($"a {label} that no longer exists");
            }
            else if (deadCount > 1)
            {
                parts.Add($"{deadCount} {label}s that no longer exist");
            }
        }

        return parts;
    }

    private static string NameOf(AlertRuleScopeEntry entry, AlertScopeNames names) => entry.Target switch
    {
        AlertRuleScopeTarget.Group => names.Groups[entry.TargetId],
        AlertRuleScopeTarget.Tag => names.Tags[entry.TargetId],
        _ => names.Domains[entry.TargetId]
    };

    private static string Natural(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => "",
        1 => parts[0],
        _ => $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}"
    };

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
