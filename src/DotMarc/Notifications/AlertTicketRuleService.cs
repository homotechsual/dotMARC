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

    public static Task SetGlobalAsync(DotMarcDbContext context, string alertType, bool createTicket, CancellationToken cancellationToken = default) =>
        UpsertAsync(context, alertType, groupId: null, createTicket, cancellationToken);

    /// <summary>Sets a group's override, or removes it with <c>null</c> so the group inherits again. Removing
    /// the row, not storing "inherit", means overrides never pile up as no-ops.</summary>
    public static async Task SetForGroupAsync(DotMarcDbContext context, int groupId, string alertType, bool? createTicket, CancellationToken cancellationToken = default)
    {
        if (createTicket is null)
        {
            RequireKnownAlertType(alertType);
            await context.AlertTicketRules
                .Where(rule => rule.GroupId == groupId && rule.AlertType == alertType)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await UpsertAsync(context, alertType, groupId, createTicket.Value, cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertAsync(DotMarcDbContext context, string alertType, int? groupId, bool createTicket, CancellationToken cancellationToken)
    {
        RequireKnownAlertType(alertType);

        var existing = await context.AlertTicketRules
            .SingleOrDefaultAsync(rule => rule.AlertType == alertType && rule.GroupId == groupId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            context.AlertTicketRules.Add(new AlertTicketRule { AlertType = alertType, GroupId = groupId, CreateTicket = createTicket });
        }
        else
        {
            existing.CreateTicket = createTicket;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void RequireKnownAlertType(string alertType)
    {
        if (AlertTypes.Find(alertType) is null)
        {
            throw new ArgumentException($"'{alertType}' is not a known alert type.", nameof(alertType));
        }
    }
}
