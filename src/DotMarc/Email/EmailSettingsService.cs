using System.Net.Mail;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Email;

/// <summary>Reads and saves how dotMARC sends email.</summary>
public static class EmailSettingsService
{
    public static Task<EmailSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.EmailSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, ISecretStore secretStore, EmailSettings updated, string? newSmtpPassword, CancellationToken cancellationToken = default)
    {
        Normalise(updated);
        Validate(updated);

        // A no-tracking snapshot, because the caller may pass the very instance this context is tracking.
        var saved = await context.EmailSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var hasNewPassword = !string.IsNullOrEmpty(newSmtpPassword);
        var changes = new AuditChanges()
            .Field("Provider", saved.Provider, updated.Provider)
            .Field("From address", saved.FromAddress, updated.FromAddress)
            .Field("From name", saved.FromName, updated.FromName)
            .Field("SMTP host", saved.SmtpHost, updated.SmtpHost)
            .Field("SMTP port", saved.SmtpPort, updated.SmtpPort)
            .Field("SMTP security", saved.SmtpSecurity, updated.SmtpSecurity)
            .Field("SMTP username", saved.SmtpUsername, updated.SmtpUsername)
            .Secret("SMTP password", hasNewPassword);
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.EmailSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.Provider = updated.Provider;
        existing.FromAddress = updated.FromAddress;
        existing.FromName = updated.FromName;
        existing.SmtpHost = updated.SmtpHost;
        existing.SmtpPort = updated.SmtpPort;
        existing.SmtpSecurity = updated.SmtpSecurity;
        existing.SmtpUsername = updated.SmtpUsername;
        if (hasNewPassword)
        {
            await secretStore.SetSecretAsync(EmailSettings.SmtpPasswordSecretKey, newSmtpPassword!, cancellationToken).ConfigureAwait(false);
            existing.SmtpPasswordConfigured = true;
        }

        AuditLog.Record(context, actor, AuditActions.EmailSettingsSaved, AuditTarget.Settings("Email"), "Saved email settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Normalise(EmailSettings settings)
    {
        settings.FromAddress = Blank(settings.FromAddress);
        settings.FromName = Blank(settings.FromName);
        settings.SmtpHost = Blank(settings.SmtpHost);
        settings.SmtpUsername = Blank(settings.SmtpUsername);
    }

    private static void Validate(EmailSettings settings)
    {
        if (settings.Provider == EmailProvider.Off)
        {
            return;
        }

        if (settings.FromAddress is null) throw new ArgumentException("From address is required to send email.", nameof(settings));
        if (!MailAddress.TryCreate(settings.FromAddress, out _) || settings.FromAddress.Length > 254) throw new ArgumentException("From address isn't a valid email address.", nameof(settings));
        if (settings.FromName is { Length: > 100 }) throw new ArgumentException("From name can be at most 100 characters.", nameof(settings));
        if (settings.Provider != EmailProvider.Smtp)
        {
            return;
        }

        if (settings.SmtpHost is null) throw new ArgumentException("SMTP host is required.", nameof(settings));
        if (settings.SmtpHost.Length > 253) throw new ArgumentException("SMTP host can be at most 253 characters.", nameof(settings));
        if (settings.SmtpPort is < 1 or > 65535) throw new ArgumentException("SMTP port must be between 1 and 65535.", nameof(settings));
        if (settings.SmtpUsername is { Length: > 254 }) throw new ArgumentException("SMTP username can be at most 254 characters.", nameof(settings));
    }
}
