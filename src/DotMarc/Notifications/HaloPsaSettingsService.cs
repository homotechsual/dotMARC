using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

/// <summary>Read/update the singleton HaloPsaSettings row. Follows NotificationSettingsService's
/// convention exactly, plus the client secret's own write path via ISecretStore - the secret
/// never travels through the HaloPsaSettings object this returns to a caller.</summary>
public static class HaloPsaSettingsService
{
    public static Task<HaloPsaSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.HaloPsaSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, ISecretStore secretStore, HaloPsaSettings updated, string? newClientSecret, CancellationToken cancellationToken = default)
    {
        // A no-tracking snapshot, because the caller may pass the very instance this context is tracking.
        var saved = await context.HaloPsaSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var hasNewClientSecret = !string.IsNullOrWhiteSpace(newClientSecret);
        var changes = new AuditChanges()
            .Field("Enabled", saved.Enabled, updated.Enabled)
            .Field("Account name", saved.AccountName, updated.AccountName)
            .Field("Auth server URL", saved.AuthServerUrl, updated.AuthServerUrl)
            .Field("Resource server URL", saved.ResourceServerUrl, updated.ResourceServerUrl)
            .Field("Client ID", saved.ClientId, updated.ClientId)
            .Field("Ticket type", Describe(saved.TicketTypeId, saved.TicketTypeName), Describe(updated.TicketTypeId, updated.TicketTypeName))
            .Field("Default priority", Describe(saved.DefaultPriorityId, saved.DefaultPriorityName), Describe(updated.DefaultPriorityId, updated.DefaultPriorityName))
            .Field("Closed status", Describe(saved.ClosedStatusId, saved.ClosedStatusName), Describe(updated.ClosedStatusId, updated.ClosedStatusName))
            .Field("Assign new tickets to", Describe(saved.AssignedAgentId, saved.AssignedAgentName) ?? "Don't assign", Describe(updated.AssignedAgentId, updated.AssignedAgentName) ?? "Don't assign")
            .Secret("Webhook secret", saved.WebhookSecret != updated.WebhookSecret)
            .Secret("Client secret", hasNewClientSecret);
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.HaloPsaSettings.SingleAsync(cancellationToken).ConfigureAwait(false);

        existing.Enabled = updated.Enabled;
        existing.AccountName = updated.AccountName;
        existing.AuthServerUrl = updated.AuthServerUrl;
        existing.ResourceServerUrl = updated.ResourceServerUrl;
        existing.ClientId = updated.ClientId;
        existing.TicketTypeId = updated.TicketTypeId;
        existing.DefaultPriorityId = updated.DefaultPriorityId;
        existing.ClosedStatusId = updated.ClosedStatusId;
        existing.AssignedAgentId = updated.AssignedAgentId;
        existing.TicketTypeName = updated.TicketTypeName;
        existing.DefaultPriorityName = updated.DefaultPriorityName;
        existing.ClosedStatusName = updated.ClosedStatusName;
        existing.AssignedAgentName = updated.AssignedAgentName;
        existing.WebhookSecret = updated.WebhookSecret;

        if (hasNewClientSecret)
        {
            await secretStore.SetSecretAsync(HaloPsaSettings.SecretStoreKey, newClientSecret!, cancellationToken).ConfigureAwait(false);
            existing.ClientSecretConfigured = true;
        }

        AuditLog.Record(context, actor, AuditActions.HaloSettingsSaved, AuditTarget.Settings("HaloPSA"), "Saved HaloPSA settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A chosen Halo option by its name, falling back to its id when the name wasn't saved.</summary>
    private static string? Describe(int? id, string? name) => id is null ? null : name ?? id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
