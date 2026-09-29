using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

/// <summary>Reads and writes <see cref="AlertTicketRule"/> rows for the two screens that edit them.
/// The decision itself lives in <see cref="AlertTicketPolicy"/>.</summary>
public static class AlertTicketRuleService
{
    /// <summary>The global rules, by alert type. A type with no entry uses its registry default.</summary>
    public static async Task<Dictionary<string, bool>> GetGlobalAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        await context.AlertTicketRules
            .AsNoTracking()
            .Where(rule => rule.GroupId == null)
            .ToDictionaryAsync(rule => rule.AlertType, rule => rule.CreateTicket, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>One group's overrides, by alert type. A type with no entry inherits the global setting.</summary>
    public static async Task<Dictionary<string, bool>> GetForGroupAsync(DotMarcDbContext context, int groupId, CancellationToken cancellationToken = default) =>
        await context.AlertTicketRules
            .AsNoTracking()
            .Where(rule => rule.GroupId == groupId)
            .ToDictionaryAsync(rule => rule.AlertType, rule => rule.CreateTicket, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>How many overrides each group has, for the badge on Manage groups. Groups with none are absent.</summary>
    public static async Task<Dictionary<int, int>> CountOverridesByGroupAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        await context.AlertTicketRules
            .AsNoTracking()
            .Where(rule => rule.GroupId != null)
            .GroupBy(rule => rule.GroupId!.Value)
            .Select(overrides => new { GroupId = overrides.Key, Count = overrides.Count() })
            .ToDictionaryAsync(overrides => overrides.GroupId, overrides => overrides.Count, cancellationToken)
            .ConfigureAwait(false);

    public static async Task SetGlobalAsync(DotMarcDbContext context, AuditActor actor, string alertType, bool createTicket, CancellationToken cancellationToken = default)
    {
        var alert = RequireKnownAlertType(alertType);
        var existing = await context.AlertTicketRules
            .SingleOrDefaultAsync(rule => rule.AlertType == alertType && rule.GroupId == null, cancellationToken)
            .ConfigureAwait(false);

        // Compared by effect: a missing rule means the alert type's default.
        var changes = new AuditChanges().Field("Creates tickets", existing?.CreateTicket ?? alert.CreatesTicketByDefault, createTicket);

        if (existing is null)
        {
            context.AlertTicketRules.Add(new AlertTicketRule { AlertType = alertType, GroupId = null, CreateTicket = createTicket });
        }
        else
        {
            existing.CreateTicket = createTicket;
        }

        if (changes.Any)
        {
            AuditLog.Record(context, actor, AuditActions.TicketRuleGlobalChanged, new AuditTarget("AlertType", alertType, alert.DisplayName),
                $"{alert.DisplayName} alerts {(createTicket ? "now create" : "no longer create")} tickets", changes);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets a group's override, or removes it with <c>null</c> so the group inherits again. Removing
    /// the row, not storing "inherit", means overrides never pile up as no-ops.</summary>
    public static async Task SetForGroupAsync(DotMarcDbContext context, AuditActor actor, int groupId, string alertType, bool? createTicket, CancellationToken cancellationToken = default)
    {
        var alert = RequireKnownAlertType(alertType);
        var existing = await context.AlertTicketRules
            .SingleOrDefaultAsync(rule => rule.GroupId == groupId && rule.AlertType == alertType, cancellationToken)
            .ConfigureAwait(false);
        var changes = new AuditChanges().Field(alert.DisplayName, OverrideText(existing?.CreateTicket), OverrideText(createTicket));
        if (!changes.Any)
        {
            return;
        }

        if (createTicket is null)
        {
            // Safe: with no existing rule, both sides are "Use default" and the method has already returned.
            context.AlertTicketRules.Remove(existing!);
        }
        else if (existing is null)
        {
            context.AlertTicketRules.Add(new AlertTicketRule { AlertType = alertType, GroupId = groupId, CreateTicket = createTicket.Value });
        }
        else
        {
            existing.CreateTicket = createTicket.Value;
        }

        var group = await context.Groups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        var target = group is null ? new AuditTarget("Group", groupId.ToString(System.Globalization.CultureInfo.InvariantCulture), null) : AuditTarget.For(group);
        AuditLog.Record(context, actor, AuditActions.TicketRuleGroupChanged, target,
            $"Set {alert.DisplayName} tickets for {group?.Name ?? $"group {groupId}"} to {OverrideText(createTicket).ToLowerInvariant()}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string OverrideText(bool? createTicket) => createTicket switch
    {
        null => "Use default",
        true => "Always create tickets",
        false => "Never create tickets"
    };

    private static AlertTypeInfo RequireKnownAlertType(string alertType) =>
        AlertTypes.Find(alertType) ?? throw new ArgumentException($"'{alertType}' is not a known alert type.", nameof(alertType));
}