using System.Globalization;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Psa.ConnectWise;

/// <summary>Reads and saves the single ConnectWise settings row. The private key goes to the secret store and is never
/// on the row; <see cref="ConnectWiseSettings.PrivateKeyConfigured"/> says whether one is saved.</summary>
public static class ConnectWiseSettingsService
{
    public static Task<ConnectWiseSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.ConnectWiseSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, ISecretStore secretStore, ConnectWiseSettings updated, string? newPrivateKey, CancellationToken cancellationToken = default)
    {
        updated.SiteUrl = NormalizeSiteUrl(updated.SiteUrl);
        updated.CompanyId = Trimmed(updated.CompanyId);
        updated.PublicKey = Trimmed(updated.PublicKey);
        updated.ClientIdOverride = Trimmed(updated.ClientIdOverride);
        if (updated.Enabled && updated.EffectiveClientId is null)
        {
            throw new ArgumentException("ConnectWise needs a client ID. Enter the one from developer.connectwise.com under Client ID.", nameof(updated));
        }

        // A no-tracking snapshot, because the caller may pass the very instance this context is tracking.
        var saved = await context.ConnectWiseSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var hasNewPrivateKey = !string.IsNullOrWhiteSpace(newPrivateKey);
        var changes = new AuditChanges()
            .Field("Enabled", saved.Enabled, updated.Enabled)
            .Field("Site", saved.SiteUrl, updated.SiteUrl)
            .Field("Company ID", saved.CompanyId, updated.CompanyId)
            .Field("Public key", saved.PublicKey, updated.PublicKey)
            .Field("Client ID override", saved.ClientIdOverride, updated.ClientIdOverride)
            .Field("Board", Describe(saved.BoardId, saved.BoardName), Describe(updated.BoardId, updated.BoardName))
            .Field("New ticket status", Describe(saved.StatusId, saved.StatusName), Describe(updated.StatusId, updated.StatusName))
            .Field("Type", Describe(saved.TypeId, saved.TypeName), Describe(updated.TypeId, updated.TypeName))
            .Field("Priority", Describe(saved.PriorityId, saved.PriorityName), Describe(updated.PriorityId, updated.PriorityName))
            .Field("Closed status", Describe(saved.ClosedStatusId, saved.ClosedStatusName), Describe(updated.ClosedStatusId, updated.ClosedStatusName))
            .Secret("Private key", hasNewPrivateKey);
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.ConnectWiseSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.Enabled = updated.Enabled;
        existing.SiteUrl = updated.SiteUrl;
        existing.CompanyId = updated.CompanyId;
        existing.PublicKey = updated.PublicKey;
        existing.ClientIdOverride = updated.ClientIdOverride;
        (existing.BoardId, existing.BoardName) = (updated.BoardId, updated.BoardName);
        (existing.StatusId, existing.StatusName) = (updated.StatusId, updated.StatusName);
        (existing.TypeId, existing.TypeName) = (updated.TypeId, updated.TypeName);
        (existing.PriorityId, existing.PriorityName) = (updated.PriorityId, updated.PriorityName);
        (existing.ClosedStatusId, existing.ClosedStatusName) = (updated.ClosedStatusId, updated.ClosedStatusName);

        if (hasNewPrivateKey)
        {
            await secretStore.SetSecretAsync(ConnectWiseSettings.PrivateKeySecretKey, newPrivateKey!.Trim(), cancellationToken).ConfigureAwait(false);
            existing.PrivateKeyConfigured = true;
        }

        AuditLog.Record(context, actor, AuditActions.ConnectWiseSettingsSaved, AuditTarget.Settings("ConnectWise"), "Saved ConnectWise settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Just the host: people paste the full URL from their browser or the API docs.</summary>
    private static string? NormalizeSiteUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return Uri.TryCreate(trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "https://" + trimmed, UriKind.Absolute, out var uri)
            ? uri.Host
            : trimmed;
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A chosen option by its name, falling back to its id when the name wasn't saved.</summary>
    private static string? Describe(int? id, string? name) => id is null ? null : name ?? id.Value.ToString(CultureInfo.InvariantCulture);
}
