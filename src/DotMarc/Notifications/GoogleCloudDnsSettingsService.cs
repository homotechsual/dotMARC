using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public static class GoogleCloudDnsSettingsService
{
    public static Task<GoogleCloudDnsSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.GoogleCloudDnsSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, ISecretStore secretStore, GoogleCloudDnsSettings updated, string? newClientSecret, CancellationToken cancellationToken = default)
    {
        // A no-tracking snapshot, because the caller may pass the very instance this context is tracking.
        var saved = await context.GoogleCloudDnsSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var hasNewClientSecret = !string.IsNullOrWhiteSpace(newClientSecret);
        var changes = new AuditChanges()
            .Field("Client ID", saved.ClientId, updated.ClientId)
            .Secret("Client secret", hasNewClientSecret);
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.GoogleCloudDnsSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.ClientId = updated.ClientId;

        if (hasNewClientSecret)
        {
            await secretStore.SetSecretAsync(GoogleCloudDnsSettings.SecretStoreKey, newClientSecret!, cancellationToken).ConfigureAwait(false);
            existing.ClientSecretConfigured = true;
        }

        AuditLog.Record(context, actor, AuditActions.GoogleCloudDnsSettingsSaved, AuditTarget.Settings("Google Cloud DNS"), "Saved Google Cloud DNS settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
