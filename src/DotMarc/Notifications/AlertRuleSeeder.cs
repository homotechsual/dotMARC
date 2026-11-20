using System.Text.Json;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

/// <summary>Creates the default pack of rules: one all-domains rule per kind, carrying its own values. A new install gets the
/// definitions' defaults. An install that ran with the old alert settings gets the values it had, copied once, so nothing it
/// configured is lost. After that the rules are the only thing alerting reads.</summary>
public static class AlertRuleSeeder
{
    /// <summary>The pack, with every parameter written out. <paramref name="settings"/> supplies the values an install already had;
    /// null means a fresh install's defaults.</summary>
    public static List<AlertRule> DefaultPack(NotificationSettings? settings = null)
    {
        settings ??= new NotificationSettings();
        return AlertRuleKinds.All
            .Select((kind, index) => new AlertRule
            {
                Kind = kind.Key,
                Name = kind.DisplayName,
                Severity = kind.DefaultSeverity,
                Enabled = kind.EnabledByDefault && AlertRuleLegacySettings.IsEnabled(kind.Key, settings),
                Position = index * 10,
                StopProcessing = true,
                DefaultKey = kind.Key,
                AutoCloseDays = AlertRuleLegacySettings.AutoCloseDays(kind.Key, settings),
                ParametersJson = JsonSerializer.Serialize(AlertRuleLegacySettings.ParametersFor(kind.Key, settings))
            })
            .ToList();
    }

    /// <summary>Adds any default rule that is missing (never touching one that exists, so an administrator's changes stay), hands
    /// alerts that have no rule to their type's default rule when it added any, then copies the settings into any default rule from an
    /// earlier version that has no values of its own.</summary>
    public static async Task EnsureDefaultPackAsync(DotMarcDbContext context, CancellationToken cancellationToken)
    {
        var settings = await context.NotificationSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? new NotificationSettings();

        if (await CreateMissingAsync(context, settings, cancellationToken).ConfigureAwait(false))
        {
            await AdoptAlertsAsync(context, cancellationToken).ConfigureAwait(false);
        }

        await MaterialiseAsync(context, settings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Two instances starting together both try the insert: the unique <see cref="AlertRule.DefaultKey"/> index makes one
    /// lose, and the loser carries on with the winner's rows. True when there was anything to add.</summary>
    private static async Task<bool> CreateMissingAsync(DotMarcDbContext context, NotificationSettings settings, CancellationToken cancellationToken)
    {
        var existingKeys = (await context.AlertRules.AsNoTracking()
                .Where(rule => rule.DefaultKey != null)
                .Select(rule => rule.DefaultKey!)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        var missing = DefaultPack(settings).Where(rule => !existingKeys.Contains(rule.DefaultKey!)).ToList();
        if (missing.Count == 0)
        {
            return false;
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

        return true;
    }

    private static async Task AdoptAlertsAsync(DotMarcDbContext context, CancellationToken cancellationToken)
    {
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

    /// <summary>A default rule with no parameters at all (null, not an empty object) is one an earlier version made, which read the
    /// settings instead. It gets the settings' values once. Having parameters is what marks it done, so a later change to the rule is
    /// never overwritten.</summary>
    private static async Task MaterialiseAsync(DotMarcDbContext context, NotificationSettings settings, CancellationToken cancellationToken)
    {
        var unmaterialised = await context.AlertRules
            .Where(rule => rule.DefaultKey != null && rule.ParametersJson == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (unmaterialised.Count == 0)
        {
            return;
        }

        foreach (var rule in unmaterialised)
        {
            rule.ParametersJson = JsonSerializer.Serialize(AlertRuleLegacySettings.ParametersFor(rule.Kind, settings));
            rule.Enabled = rule.Enabled && AlertRuleLegacySettings.IsEnabled(rule.Kind, settings);
            rule.AutoCloseDays ??= AlertRuleLegacySettings.AutoCloseDays(rule.Kind, settings);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
