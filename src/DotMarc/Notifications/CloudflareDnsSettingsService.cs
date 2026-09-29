using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public static class CloudflareDnsSettingsService
{
    public static Task<CloudflareDnsSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.CloudflareDnsSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, ISecretStore secretStore, CloudflareDnsSettings updated, string? newClientSecret, CancellationToken cancellationToken = default)
    {
        // A no-tracking snapshot, because the caller may pass the very instance this context is tracking.
        var saved = await context.CloudflareDnsSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var hasNewClientSecret = !string.IsNullOrWhiteSpace(newClientSecret);
        var changes = new AuditChanges()
            .Field("Client ID", saved.ClientId, updated.ClientId)
            .Secret("Client secret", hasNewClientSecret);
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.CloudflareDnsSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.ClientId = updated.ClientId;

        if (hasNewClientSecret)
        {
            await secretStore.SetSecretAsync(CloudflareDnsSettings.SecretStoreKey, newClientSecret!, cancellationToken).ConfigureAwait(false);
            existing.ClientSecretConfigured = true;
        }

        AuditLog.Record(context, actor, AuditActions.CloudflareDnsSettingsSaved, AuditTarget.Settings("Cloudflare DNS"), "Saved Cloudflare DNS settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
