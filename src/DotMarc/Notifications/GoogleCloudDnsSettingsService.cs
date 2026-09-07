using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public static class GoogleCloudDnsSettingsService
{
    public static Task<GoogleCloudDnsSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.GoogleCloudDnsSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, ISecretStore secretStore, GoogleCloudDnsSettings updated, string? newClientSecret, CancellationToken cancellationToken = default)
    {
        var existing = await context.GoogleCloudDnsSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.ClientId = updated.ClientId;

        if (!string.IsNullOrWhiteSpace(newClientSecret))
        {
            await secretStore.SetSecretAsync(GoogleCloudDnsSettings.SecretStoreKey, newClientSecret, cancellationToken).ConfigureAwait(false);
            existing.ClientSecretConfigured = true;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
