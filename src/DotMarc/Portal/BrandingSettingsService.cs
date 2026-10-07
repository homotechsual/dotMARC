using System.Net.Mail;
using System.Security.Cryptography;
using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Portal;

public sealed record BrandingImageUpload(Guid? ImageId, string? Problem);

/// <summary>Reads and saves the MSP-wide brand the client portal uses, and stores uploaded logos.</summary>
public static class BrandingSettingsService
{
    public static Task<BrandingSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.BrandingSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, BrandingSettings updated, CancellationToken cancellationToken = default)
    {
        Normalise(updated);
        Validate(updated);

        var saved = await context.BrandingSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges()
            .Field("Product name", saved.ProductName, updated.ProductName)
            .Field("Primary colour", saved.PrimaryColour, updated.PrimaryColour)
            .Field("Secondary colour", saved.SecondaryColour, updated.SecondaryColour)
            .Field("Logo", LogoState(saved.LogoImageId, saved.LogoImageId), LogoState(saved.LogoImageId, updated.LogoImageId))
            .Field("Dark logo", LogoState(saved.DarkLogoImageId, saved.DarkLogoImageId), LogoState(saved.DarkLogoImageId, updated.DarkLogoImageId))
            .Field("Support email", saved.SupportEmail, updated.SupportEmail)
            .Field("Support URL", saved.SupportUrl, updated.SupportUrl)
            .Field("Support phone", saved.SupportPhone, updated.SupportPhone)
            .Field("Footer text", saved.FooterText, updated.FooterText);
        if (!changes.Any)
        {
            return;
        }

        // The tracked row may be the very instance passed in (read with GetAsync, then edited), so copy onto it only
        // when it's a different one.
        var existing = await context.BrandingSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        if (!ReferenceEquals(existing, updated))
        {
            context.Entry(existing).CurrentValues.SetValues(updated);
        }

        existing.Id = 1;
        AuditLog.Record(context, actor, AuditActions.BrandingSettingsSaved, AuditTarget.Settings("Branding"), "Saved branding", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BrandingImageCleanup.DeleteUnreferencedAsync(context, new[] { saved.LogoImageId, saved.DarkLogoImageId }.OfType<Guid>(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores a validated logo under a new id and returns it, or says why it can't be used. Storing isn't
    /// audited on its own: the logo only takes effect, and is audited, when branding using it is saved.</summary>
    public static async Task<BrandingImageUpload> UploadImageAsync(DotMarcDbContext context, AuditActor actor, byte[] bytes, CancellationToken cancellationToken = default)
    {
        var check = BrandingImages.Validate(bytes);
        if (check.ContentType is null)
        {
            return new BrandingImageUpload(null, check.Problem);
        }

        var image = new BrandingImage
        {
            Id = Guid.NewGuid(), ContentType = check.ContentType, Bytes = bytes,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), UploadedUtc = DateTimeOffset.UtcNow,
        };
        context.BrandingImages.Add(image);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new BrandingImageUpload(image.Id, null);
    }

    // "None", "Set", or "Replaced" when a different image takes the place of the saved one.
    private static string LogoState(Guid? savedId, Guid? id) =>
        id is null ? "None" : savedId is null || savedId == id ? "Set" : "Replaced";

    private static void Normalise(BrandingSettings settings)
    {
        settings.ProductName = settings.ProductName?.Trim() ?? "";
        settings.PrimaryColour = settings.PrimaryColour?.Trim().ToUpperInvariant() ?? "";
        settings.SecondaryColour = settings.SecondaryColour?.Trim().ToUpperInvariant() ?? "";
        settings.SupportEmail = Blank(settings.SupportEmail);
        settings.SupportUrl = Blank(settings.SupportUrl);
        settings.SupportPhone = Blank(settings.SupportPhone);
        settings.FooterText = Blank(settings.FooterText);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Validate(BrandingSettings settings)
    {
        if (settings.ProductName.Length == 0) throw new ArgumentException("Product name can't be empty.", nameof(settings));
        if (settings.ProductName.Length > 60) throw new ArgumentException("Product name can be at most 60 characters.", nameof(settings));
        if (!BrandColours.IsValid(settings.PrimaryColour)) throw new ArgumentException("Primary colour must be a hex colour such as #1A73E8.", nameof(settings));
        if (!BrandColours.IsValid(settings.SecondaryColour)) throw new ArgumentException("Secondary colour must be a hex colour such as #1A73E8.", nameof(settings));
        if (settings.SupportEmail is { } email && !MailAddress.TryCreate(email, out _)) throw new ArgumentException("Support email isn't a valid email address.", nameof(settings));
        if (settings.SupportEmail is { Length: > 254 }) throw new ArgumentException("Support email can be at most 254 characters.", nameof(settings));
        if (settings.SupportUrl is { } url && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)) throw new ArgumentException("Support URL must be an absolute https:// address.", nameof(settings));
        if (settings.SupportUrl is { Length: > 500 }) throw new ArgumentException("Support URL can be at most 500 characters.", nameof(settings));
        if (settings.SupportPhone is { Length: > 40 }) throw new ArgumentException("Support phone can be at most 40 characters.", nameof(settings));
        if (settings.FooterText is { Length: > 200 }) throw new ArgumentException("Footer text can be at most 200 characters.", nameof(settings));
    }
}
