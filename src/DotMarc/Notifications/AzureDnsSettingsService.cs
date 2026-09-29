using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public static class AzureDnsSettingsService
{
    public static Task<AzureDnsSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.AzureDnsSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, ISecretStore secretStore, AzureDnsSettings updated, string? newClientSecret, CancellationToken cancellationToken = default)
    {
        // A no-tracking snapshot, because the caller may pass the very instance this context is tracking.
        var saved = await context.AzureDnsSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var hasNewClientSecret = !string.IsNullOrWhiteSpace(newClientSecret);
        var changes = new AuditChanges()
            .Field("Client ID", saved.ClientId, updated.ClientId)
            .Secret("Client secret", hasNewClientSecret);
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.AzureDnsSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.ClientId = updated.ClientId;

        if (hasNewClientSecret)
        {
            await secretStore.SetSecretAsync(AzureDnsSettings.SecretStoreKey, newClientSecret!, cancellationToken).ConfigureAwait(false);
            existing.ClientSecretConfigured = true;
        }

        AuditLog.Record(context, actor, AuditActions.AzureDnsSettingsSaved, AuditTarget.Settings("Azure DNS"), "Saved Azure DNS settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
