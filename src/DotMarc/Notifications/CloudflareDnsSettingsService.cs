using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public static class CloudflareDnsSettingsService
{
    public static Task<CloudflareDnsSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.CloudflareDnsSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, ISecretStore secretStore, CloudflareDnsSettings updated, string? newClientSecret, CancellationToken cancellationToken = default)
    {
        var existing = await context.CloudflareDnsSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.ClientId = updated.ClientId;

        if (!string.IsNullOrWhiteSpace(newClientSecret))
        {
            await secretStore.SetSecretAsync(CloudflareDnsSettings.SecretStoreKey, newClientSecret, cancellationToken).ConfigureAwait(false);
            existing.ClientSecretConfigured = true;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
