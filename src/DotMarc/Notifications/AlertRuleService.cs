using System.Globalization;
using System.Text.Json;
using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

/// <summary>A rule change was refused. <see cref="Problems"/> are sentences a person can act on, each naming the field.</summary>
public sealed class AlertRuleValidationException(IReadOnlyList<string> problems) : Exception(string.Join(" ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>The one way to create, change, order and delete alert rules: validated against the kind's description and recorded in
/// the audit log. Who may do it is decided by the caller (the Manage alert rules permission); this does not check.</summary>
public static class AlertRuleService
{
    private const int PositionStep = 10;

    public static async Task<AlertRule> CreateAsync(DotMarcDbContext context, AuditActor actor, string kind, AlertRuleInput input, CancellationToken cancellationToken = default)
    {
        var problems = AlertRuleValidator.Problems(kind, input).ToList();
        await AddDuplicateNameProblemAsync(context, kind, input.Name, exceptRuleId: null, problems, cancellationToken).ConfigureAwait(false);
        if (problems.Count > 0)
        {
            throw new AlertRuleValidationException(problems);
        }

        // New rules go first, so a rule made for one client applies before the default does.
        var lowestPosition = await context.AlertRules.Where(rule => rule.Kind == kind).MinAsync(rule => (int?)rule.Position, cancellationToken).ConfigureAwait(false);
        var created = new AlertRule
        {
            Kind = kind,
            Name = input.Name.Trim(),
            Enabled = input.Enabled,
            Severity = input.Severity,
            Position = (lowestPosition ?? PositionStep) - PositionStep,
            StopProcessing = input.StopProcessing,
            CooldownMinutes = input.CooldownMinutes,
            AutoCloseDays = input.AutoCloseDays,
            ParametersJson = JsonSerializer.Serialize(FullParameters(kind, input.Parameters)),
            Scope = [.. input.Scope.Select(entry => new AlertRuleScopeEntry { Target = entry.Target, TargetId = entry.TargetId, Exclude = entry.Exclude })]
        };
        context.AlertRules.Add(created);
        await AuditLog.SaveAndRecordAsync(
            context,
            () => AuditLog.Record(context, actor, AuditActions.AlertRuleCreated, AuditTarget.For(created), $"Created the alert rule '{created.Name}'"),
            cancellationToken).ConfigureAwait(false);
        return created;
    }

    public static async Task UpdateAsync(DotMarcDbContext context, AuditActor actor, int ruleId, AlertRuleInput input, CancellationToken cancellationToken = default)
    {
        var rule = await LoadAsync(context, ruleId, cancellationToken).ConfigureAwait(false);
        var problems = AlertRuleValidator.Problems(rule.Kind, input).ToList();
        await AddDuplicateNameProblemAsync(context, rule.Kind, input.Name, rule.Id, problems, cancellationToken).ConfigureAwait(false);
        if (problems.Count > 0)
        {
            throw new AlertRuleValidationException(problems);
        }

        var oldParameters = ParseParameters(rule.ParametersJson);
        var newParameters = FullParameters(rule.Kind, input.Parameters);
        var names = await ScopeNamesAsync(context, rule.Scope.Select(entry => (entry.Target, entry.TargetId)).Concat(input.Scope.Select(entry => (entry.Target, entry.TargetId))), cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges()
            .Field("Name", rule.Name, input.Name.Trim())
            .Field("Enabled", rule.Enabled, input.Enabled)
            .Field("Severity", rule.Severity, input.Severity)
            .Field("Stop processing", rule.StopProcessing, input.StopProcessing)
            .Field("Cooldown (minutes)", rule.CooldownMinutes, input.CooldownMinutes)
            .Field("Close after (days)", rule.AutoCloseDays, input.AutoCloseDays);
        foreach (var definition in AlertRuleKinds.Find(rule.Kind)!.Parameters)
        {
            changes.Field(definition.Label, oldParameters.GetValueOrDefault(definition.Key, definition.Default), newParameters[definition.Key]);
        }

        changes.Set("Scope", rule.Scope.Select(entry => Describe(entry.Target, entry.TargetId, entry.Exclude, names)), input.Scope.Select(entry => Describe(entry.Target, entry.TargetId, entry.Exclude, names)));
        if (!changes.Any)
        {
            return;
        }

        rule.Name = input.Name.Trim();
        rule.Enabled = input.Enabled;
        rule.Severity = input.Severity;
        rule.StopProcessing = input.StopProcessing;
        rule.CooldownMinutes = input.CooldownMinutes;
        rule.AutoCloseDays = input.AutoCloseDays;
        rule.ParametersJson = JsonSerializer.Serialize(newParameters);
        ReplaceScope(rule, input.Scope);

        AuditLog.Record(context, actor, AuditActions.AlertRuleChanged, AuditTarget.For(rule), $"Changed the alert rule '{rule.Name}'", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a rule you made. A default rule can be switched off or reset but not deleted. The alerts it raised stay, with
    /// no rule.</summary>
    public static async Task DeleteAsync(DotMarcDbContext context, AuditActor actor, int ruleId, CancellationToken cancellationToken = default)
    {
        var rule = await LoadAsync(context, ruleId, cancellationToken).ConfigureAwait(false);
        if (rule.DefaultKey is not null)
        {
            throw new AlertRuleValidationException([$"The default rule '{rule.Name}' can't be deleted. Switch it off, or reset it to its default values."]);
        }

        AuditLog.Record(context, actor, AuditActions.AlertRuleDeleted, AuditTarget.For(rule), $"Deleted the alert rule '{rule.Name}'");
        context.AlertRules.Remove(rule);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves a rule one place earlier (direction -1) or later (+1) among the rules of its kind. At either end it does
    /// nothing. Rules that somehow share a position are renumbered first, so the order is always unambiguous.</summary>
    public static async Task MoveAsync(DotMarcDbContext context, AuditActor actor, int ruleId, int direction, CancellationToken cancellationToken = default)
    {
        var rule = await LoadAsync(context, ruleId, cancellationToken).ConfigureAwait(false);
        var siblings = await context.AlertRules.Where(candidate => candidate.Kind == rule.Kind)
            .OrderBy(candidate => candidate.Position).ThenBy(candidate => candidate.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var index = siblings.FindIndex(candidate => candidate.Id == rule.Id);
        var target = index + Math.Sign(direction);
        if (direction == 0 || target < 0 || target >= siblings.Count)
        {
            return;
        }

        if (siblings.Select(candidate => candidate.Position).Distinct().Count() != siblings.Count)
        {
            for (var position = 0; position < siblings.Count; position++)
            {
                siblings[position].Position = (position + 1) * PositionStep;
            }
        }

        (siblings[index].Position, siblings[target].Position) = (siblings[target].Position, siblings[index].Position);

        var changes = new AuditChanges().Field("Order", index + 1, target + 1);
        AuditLog.Record(context, actor, AuditActions.AlertRuleMoved, AuditTarget.For(rule), $"Moved the alert rule '{rule.Name}' {(direction < 0 ? "earlier" : "later")}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Puts a default rule back to what dotMARC ships: its name, severity, switched on, stopping processing, no cooldown or
    /// auto-close override, no scope, and every parameter at its default. Its place in the order stays.</summary>
    public static async Task ResetToDefaultAsync(DotMarcDbContext context, AuditActor actor, int ruleId, CancellationToken cancellationToken = default)
    {
        var rule = await LoadAsync(context, ruleId, cancellationToken).ConfigureAwait(false);
        if (rule.DefaultKey is null)
        {
            throw new AlertRuleValidationException([$"'{rule.Name}' is not a default rule, so there are no default values to go back to."]);
        }

        var kind = AlertRuleKinds.Find(rule.Kind)!;
        var shipped = new AlertRuleInput(kind.DisplayName, Enabled: true, kind.DefaultSeverity, StopProcessing: true, CooldownMinutes: null, AutoCloseDays: null,
            kind.Parameters.ToDictionary(definition => definition.Key, definition => definition.Default), []);
        var oldParameters = ParseParameters(rule.ParametersJson);
        var names = await ScopeNamesAsync(context, rule.Scope.Select(entry => (entry.Target, entry.TargetId)), cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges()
            .Field("Name", rule.Name, shipped.Name)
            .Field("Enabled", rule.Enabled, shipped.Enabled)
            .Field("Severity", rule.Severity, shipped.Severity)
            .Field("Stop processing", rule.StopProcessing, shipped.StopProcessing)
            .Field("Cooldown (minutes)", rule.CooldownMinutes, shipped.CooldownMinutes)
            .Field("Close after (days)", rule.AutoCloseDays, shipped.AutoCloseDays);
        foreach (var definition in kind.Parameters)
        {
            changes.Field(definition.Label, oldParameters.GetValueOrDefault(definition.Key, definition.Default), definition.Default);
        }

        changes.Set("Scope", rule.Scope.Select(entry => Describe(entry.Target, entry.TargetId, entry.Exclude, names)), []);

        rule.Name = shipped.Name;
        rule.Enabled = true;
        rule.Severity = shipped.Severity;
        rule.StopProcessing = true;
        rule.CooldownMinutes = null;
        rule.AutoCloseDays = null;
        rule.ParametersJson = JsonSerializer.Serialize(shipped.Parameters);
        ReplaceScope(rule, []);

        AuditLog.Record(context, actor, AuditActions.AlertRuleReset, AuditTarget.For(rule), $"Reset the alert rule '{rule.Name}' to its default values", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes scope entries that point at a group, tag or domain that no longer exists, auditing each rule it cleans.
    /// Returns how many entries it removed.</summary>
    public static async Task<int> PruneDeadScopeAsync(DotMarcDbContext context, AuditActor actor, CancellationToken cancellationToken = default)
    {
        var dead = await FindDeadScopeAsync(context, tracked: true, cancellationToken).ConfigureAwait(false);
        if (dead.Count == 0)
        {
            return 0;
        }

        foreach (var ruleGroup in dead.GroupBy(entry => entry.AlertRuleId))
        {
            var rule = await context.AlertRules.AsNoTracking().SingleAsync(candidate => candidate.Id == ruleGroup.Key, cancellationToken).ConfigureAwait(false);
            var removed = ruleGroup.Select(entry => Describe(entry.Target, entry.TargetId, entry.Exclude, new Dictionary<(AlertRuleScopeTarget, int), string>())).ToList();
            AuditLog.Record(context, actor, AuditActions.AlertRuleScopePruned, AuditTarget.For(rule),
                $"Removed {removed.Count} scope {(removed.Count == 1 ? "entry" : "entries")} from the alert rule '{rule.Name}' for things that no longer exist",
                new AuditChanges().Set("Scope", removed, []));
        }

        context.AlertRuleScopeEntries.RemoveRange(dead);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return dead.Count;
    }

    /// <summary>The scope entries that point at something that no longer exists. A read: it changes nothing.</summary>
    public static async Task<IReadOnlyList<AlertRuleScopeEntry>> ListDeadScopeAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        await FindDeadScopeAsync(context, tracked: false, cancellationToken).ConfigureAwait(false);

    private static async Task<List<AlertRuleScopeEntry>> FindDeadScopeAsync(DotMarcDbContext context, bool tracked, CancellationToken cancellationToken)
    {
        var query = tracked ? context.AlertRuleScopeEntries.AsQueryable() : context.AlertRuleScopeEntries.AsNoTracking();
        var entries = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        var liveGroups = (await context.Groups.AsNoTracking().Select(group => group.Id).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var liveTags = (await context.Tags.AsNoTracking().Select(tag => tag.Id).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var liveDomains = (await context.Domains.AsNoTracking().Select(domain => domain.Id).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        return entries.Where(entry => entry.Target switch
        {
            AlertRuleScopeTarget.Group => !liveGroups.Contains(entry.TargetId),
            AlertRuleScopeTarget.Tag => !liveTags.Contains(entry.TargetId),
            _ => !liveDomains.Contains(entry.TargetId)
        }).ToList();
    }

    private static async Task<AlertRule> LoadAsync(DotMarcDbContext context, int ruleId, CancellationToken cancellationToken) =>
        await context.AlertRules.Include(rule => rule.Scope).SingleOrDefaultAsync(rule => rule.Id == ruleId, cancellationToken).ConfigureAwait(false)
        ?? throw new AlertRuleValidationException(["That alert rule no longer exists."]);

    private static async Task AddDuplicateNameProblemAsync(DotMarcDbContext context, string kind, string name, int? exceptRuleId, List<string> problems, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || AlertRuleKinds.Find(kind) is null)
        {
            return;
        }

        var lowered = name.Trim().ToLower();
        if (await context.AlertRules.AnyAsync(rule => rule.Kind == kind && rule.Id != exceptRuleId && rule.Name.ToLower() == lowered, cancellationToken).ConfigureAwait(false))
        {
            problems.Add("Another rule for this kind of alert already has that name.");
        }
    }

    /// <summary>Every parameter the kind defines, with the value given or else the default, so a stored rule is always explicit.</summary>
    private static Dictionary<string, string> FullParameters(string kind, IReadOnlyDictionary<string, string> given) =>
        AlertRuleKinds.Find(kind)!.Parameters.ToDictionary(definition => definition.Key, definition => given.TryGetValue(definition.Key, out var value) ? value : definition.Default);

    private static Dictionary<string, string> ParseParameters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Makes the rule's scope match the wanted entries, adding and removing only the difference (so an entry that stays is
    /// never deleted and re-added, which the unique index would not like).</summary>
    private static void ReplaceScope(AlertRule rule, IReadOnlyList<AlertRuleScopeInput> wanted)
    {
        foreach (var entry in rule.Scope.Where(entry => !wanted.Any(candidate => Same(entry, candidate))).ToList())
        {
            rule.Scope.Remove(entry);
        }

        foreach (var entry in wanted.Where(candidate => !rule.Scope.Any(existing => Same(existing, candidate))))
        {
            rule.Scope.Add(new AlertRuleScopeEntry { Target = entry.Target, TargetId = entry.TargetId, Exclude = entry.Exclude });
        }
    }

    private static bool Same(AlertRuleScopeEntry entry, AlertRuleScopeInput candidate) =>
        (entry.Target, entry.TargetId, entry.Exclude) == (candidate.Target, candidate.TargetId, candidate.Exclude);

    private static async Task<Dictionary<(AlertRuleScopeTarget, int), string>> ScopeNamesAsync(
        DotMarcDbContext context, IEnumerable<(AlertRuleScopeTarget Target, int TargetId)> targets, CancellationToken cancellationToken)
    {
        var wanted = targets.Distinct().ToList();
        var names = new Dictionary<(AlertRuleScopeTarget, int), string>();
        var groupIds = wanted.Where(target => target.Target == AlertRuleScopeTarget.Group).Select(target => target.TargetId).ToList();
        var tagIds = wanted.Where(target => target.Target == AlertRuleScopeTarget.Tag).Select(target => target.TargetId).ToList();
        var domainIds = wanted.Where(target => target.Target == AlertRuleScopeTarget.Domain).Select(target => target.TargetId).ToList();
        foreach (var group in await context.Groups.AsNoTracking().Where(group => groupIds.Contains(group.Id)).Select(group => new { group.Id, group.Name }).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            names[(AlertRuleScopeTarget.Group, group.Id)] = group.Name;
        }

        foreach (var tag in await context.Tags.AsNoTracking().Where(tag => tagIds.Contains(tag.Id)).Select(tag => new { tag.Id, tag.Name }).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            names[(AlertRuleScopeTarget.Tag, tag.Id)] = tag.Name;
        }

        foreach (var domain in await context.Domains.AsNoTracking().Where(domain => domainIds.Contains(domain.Id)).Select(domain => new { domain.Id, domain.Name }).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            names[(AlertRuleScopeTarget.Domain, domain.Id)] = domain.Name;
        }

        return names;
    }

    private static string Describe(AlertRuleScopeTarget target, int targetId, bool exclude, IReadOnlyDictionary<(AlertRuleScopeTarget, int), string> names)
    {
        var name = names.TryGetValue((target, targetId), out var found) ? found : $"#{targetId.ToString(CultureInfo.InvariantCulture)} (gone)";
        return $"{(exclude ? "except" : "include")} {target.ToString().ToLowerInvariant()} {name}";
    }
}
