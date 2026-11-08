using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Psa.SuperOps;

/// <summary>Reads and saves the single SuperOps settings row. The API token goes to the secret store and is never on the
/// row; <see cref="SuperOpsSettings.ApiTokenConfigured"/> says whether one is saved.</summary>
public static class SuperOpsSettingsService
{
    public static Task<SuperOpsSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.SuperOpsSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, ISecretStore secretStore, SuperOpsSettings updated, string? newApiToken, CancellationToken cancellationToken = default)
    {
        updated.Subdomain = NormalizeSubdomain(updated.Subdomain);
        updated.Region = SuperOpsRegions.All.FirstOrDefault(region => string.Equals(region, updated.Region, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Choose the SuperOps region your account is in.", nameof(updated));
        updated.RequestType = Trimmed(updated.RequestType);
        updated.NewStatus = Trimmed(updated.NewStatus);
        updated.ClosedStatus = Trimmed(updated.ClosedStatus);
        updated.Priority = Trimmed(updated.Priority);
        updated.Urgency = Trimmed(updated.Urgency);
        updated.Category = Trimmed(updated.Category);

        // A no-tracking snapshot, because the caller may pass the very instance this context is tracking.
        var saved = await context.SuperOpsSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var hasNewApiToken = !string.IsNullOrWhiteSpace(newApiToken);
        var changes = new AuditChanges()
            .Field("Enabled", saved.Enabled, updated.Enabled)
            .Field("Region", saved.Region, updated.Region)
            .Field("Subdomain", saved.Subdomain, updated.Subdomain)
            .Field("Request type", saved.RequestType, updated.RequestType)
            .Field("New ticket status", saved.NewStatus, updated.NewStatus)
            .Field("Closed status", saved.ClosedStatus, updated.ClosedStatus)
            .Field("Priority", saved.Priority, updated.Priority)
            .Field("Urgency", saved.Urgency, updated.Urgency)
            .Field("Category", saved.Category, updated.Category)
            .Secret("API token", hasNewApiToken);
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.SuperOpsSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.Enabled = updated.Enabled;
        existing.Region = updated.Region;
        existing.Subdomain = updated.Subdomain;
        existing.RequestType = updated.RequestType;
        existing.NewStatus = updated.NewStatus;
        existing.ClosedStatus = updated.ClosedStatus;
        existing.Priority = updated.Priority;
        existing.Urgency = updated.Urgency;
        existing.Category = updated.Category;

        if (hasNewApiToken)
        {
            await secretStore.SetSecretAsync(SuperOpsSettings.ApiTokenSecretKey, newApiToken!.Trim(), cancellationToken).ConfigureAwait(false);
            existing.ApiTokenConfigured = true;
        }

        AuditLog.Record(context, actor, AuditActions.SuperOpsSettingsSaved, AuditTarget.Settings("SuperOps"), "Saved SuperOps settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Just the subdomain: people paste the whole address (acme.superops.ai or https://acme.superops.ai/...).</summary>
    private static string? NormalizeSubdomain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var host = Uri.TryCreate(trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "https://" + trimmed, UriKind.Absolute, out var uri)
            ? uri.Host
            : trimmed;
        var dot = host.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? host[..dot] : host;
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
