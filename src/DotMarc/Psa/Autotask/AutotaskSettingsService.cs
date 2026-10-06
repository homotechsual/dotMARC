using System.Globalization;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Psa.Autotask;

/// <summary>Reads and saves the single Autotask settings row. The secret goes to the secret store and is never on the
/// row; <see cref="AutotaskSettings.SecretConfigured"/> says whether one is saved.</summary>
public static class AutotaskSettingsService
{
    public static Task<AutotaskSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.AutotaskSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, ISecretStore secretStore, AutotaskSettings updated, string? newSecret, CancellationToken cancellationToken = default)
    {
        updated.Username = Trimmed(updated.Username);
        updated.IntegrationCodeOverride = Trimmed(updated.IntegrationCodeOverride);
        if (updated.Enabled && updated.EffectiveIntegrationCode is null)
        {
            throw new ArgumentException("Autotask needs an API tracking identifier. Choose one when you create the API user, then enter it under API tracking identifier.", nameof(updated));
        }

        // A no-tracking snapshot, because the caller may pass the very instance this context is tracking.
        var saved = await context.AutotaskSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var hasNewSecret = !string.IsNullOrWhiteSpace(newSecret);
        var changes = new AuditChanges()
            .Field("Enabled", saved.Enabled, updated.Enabled)
            .Field("Username", saved.Username, updated.Username)
            .Field("API tracking identifier override", saved.IntegrationCodeOverride, updated.IntegrationCodeOverride)
            .Field("Queue", Describe(saved.QueueId, saved.QueueName), Describe(updated.QueueId, updated.QueueName))
            .Field("Ticket type", Describe(saved.TicketTypeId, saved.TicketTypeName), Describe(updated.TicketTypeId, updated.TicketTypeName))
            .Field("Issue type", Describe(saved.IssueTypeId, saved.IssueTypeName), Describe(updated.IssueTypeId, updated.IssueTypeName))
            .Field("Priority", Describe(saved.PriorityId, saved.PriorityName), Describe(updated.PriorityId, updated.PriorityName))
            .Field("Closed status", Describe(saved.ClosedStatusId, saved.ClosedStatusName), Describe(updated.ClosedStatusId, updated.ClosedStatusName))
            .Secret("Secret", hasNewSecret);
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.AutotaskSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.Enabled = updated.Enabled;
        existing.Username = updated.Username;
        existing.IntegrationCodeOverride = updated.IntegrationCodeOverride;
        (existing.QueueId, existing.QueueName) = (updated.QueueId, updated.QueueName);
        (existing.TicketTypeId, existing.TicketTypeName) = (updated.TicketTypeId, updated.TicketTypeName);
        (existing.IssueTypeId, existing.IssueTypeName) = (updated.IssueTypeId, updated.IssueTypeName);
        (existing.PriorityId, existing.PriorityName) = (updated.PriorityId, updated.PriorityName);
        (existing.ClosedStatusId, existing.ClosedStatusName) = (updated.ClosedStatusId, updated.ClosedStatusName);

        if (hasNewSecret)
        {
            await secretStore.SetSecretAsync(AutotaskSettings.SecretStoreKey, newSecret!.Trim(), cancellationToken).ConfigureAwait(false);
            existing.SecretConfigured = true;
        }

        AuditLog.Record(context, actor, AuditActions.AutotaskSettingsSaved, AuditTarget.Settings("Autotask"), "Saved Autotask settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A chosen option by its name, falling back to its id when the name wasn't saved.</summary>
    private static string? Describe(int? id, string? name) => id is null ? null : name ?? id.Value.ToString(CultureInfo.InvariantCulture);
}
