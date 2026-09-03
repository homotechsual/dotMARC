using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

/// <summary>Read/update the singleton HaloPsaSettings row. Follows NotificationSettingsService's
/// convention exactly, plus the client secret's own write path via IHaloSecretStore — the secret
/// never travels through the HaloPsaSettings object this returns to a caller.</summary>
public static class HaloPsaSettingsService
{
    public static Task<HaloPsaSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.HaloPsaSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, IHaloSecretStore secretStore, HaloPsaSettings updated, string? newClientSecret, CancellationToken cancellationToken = default)
    {
        var existing = await context.HaloPsaSettings.SingleAsync(cancellationToken).ConfigureAwait(false);

        existing.Enabled = updated.Enabled;
        existing.AccountName = updated.AccountName;
        existing.AuthServerUrl = updated.AuthServerUrl;
        existing.ResourceServerUrl = updated.ResourceServerUrl;
        existing.ClientId = updated.ClientId;
        existing.TicketTypeId = updated.TicketTypeId;
        existing.DefaultPriorityId = updated.DefaultPriorityId;
        existing.ClosedStatusId = updated.ClosedStatusId;
        existing.WebhookSecret = updated.WebhookSecret;

        if (!string.IsNullOrWhiteSpace(newClientSecret))
        {
            await secretStore.SetClientSecretAsync(newClientSecret, cancellationToken).ConfigureAwait(false);
            existing.ClientSecretConfigured = true;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
