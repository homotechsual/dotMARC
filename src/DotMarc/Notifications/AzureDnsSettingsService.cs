using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public static class AzureDnsSettingsService
{
    public static Task<AzureDnsSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.AzureDnsSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, ISecretStore secretStore, AzureDnsSettings updated, string? newClientSecret, CancellationToken cancellationToken = default)
    {
        var existing = await context.AzureDnsSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.TenantId = updated.TenantId;
        existing.ClientId = updated.ClientId;

        if (!string.IsNullOrWhiteSpace(newClientSecret))
        {
            await secretStore.SetSecretAsync(AzureDnsSettings.SecretStoreKey, newClientSecret, cancellationToken).ConfigureAwait(false);
            existing.ClientSecretConfigured = true;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
