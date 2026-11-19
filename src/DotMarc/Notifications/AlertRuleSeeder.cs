using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

/// <summary>Creates the default pack of rules: one all-domains rule per kind, which carries no parameter values of its own, so
/// each parameter falls back to the global settings and the pack behaves as alerting did before rules existed.</summary>
public static class AlertRuleSeeder
{
    public static List<AlertRule> DefaultPack() => AlertRuleKinds.All
        .Select((kind, index) => new AlertRule
        {
            Kind = kind.Key,
            Name = kind.DisplayName,
            Severity = kind.DefaultSeverity,
            Position = index * 10,
            StopProcessing = true,
            DefaultKey = kind.Key
        })
        .ToList();

    /// <summary>Adds any default rule that is missing (never touching one that exists, so an administrator's changes stay), and when
    /// it added any, points alerts that have no rule at their type's default rule. Two instances starting together both try the
    /// insert: the unique <see cref="AlertRule.DefaultKey"/> index makes one lose, and the loser carries on with the winner's rows.</summary>
    public static async Task EnsureDefaultPackAsync(DotMarcDbContext context, CancellationToken cancellationToken)
    {
        var existingKeys = (await context.AlertRules.AsNoTracking()
                .Where(rule => rule.DefaultKey != null)
                .Select(rule => rule.DefaultKey!)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        var missing = DefaultPack().Where(rule => !existingKeys.Contains(rule.DefaultKey!)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        context.AlertRules.AddRange(missing);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Another instance seeded first. Forget our copies; the winner's rows are what is stored.
            foreach (var rule in missing)
            {
                context.Entry(rule).State = EntityState.Detached;
            }
        }

        var defaultRules = await context.AlertRules.AsNoTracking()
            .Where(rule => rule.DefaultKey != null)
            .Select(rule => new { rule.Id, rule.Kind })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var defaultRule in defaultRules)
        {
            var kind = defaultRule.Kind;
            var ruleId = defaultRule.Id;
            await context.AlertEvents
                .Where(alert => alert.AlertRuleId == null && alert.AlertType == kind)
                .ExecuteUpdateAsync(setters => setters.SetProperty(alert => alert.AlertRuleId, ruleId), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
