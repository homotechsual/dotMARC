using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Portal;

/// <summary>The brand a portal user sees, after layering their Groups over the MSP default.</summary>
public sealed record ResolvedBrand(string ProductName, string Heading, string PrimaryColour, string SecondaryColour,
    Guid? LogoImageId, Guid? DarkLogoImageId, string? SupportEmail, string? SupportUrl, string? SupportPhone, string? FooterText);

public sealed record ScopedGroupBrand(string GroupName);

/// <summary>Works out the brand a portal user sees from the MSP default and their scoped Groups.</summary>
public static class PortalBranding
{
    public static ResolvedBrand Resolve(BrandingSettings msp, IReadOnlyList<ScopedGroupBrand> scopedGroups)
    {
        var heading = scopedGroups.Count == 1 ? scopedGroups[0].GroupName : msp.ProductName;
        return new ResolvedBrand(msp.ProductName, heading, msp.PrimaryColour, msp.SecondaryColour,
            msp.LogoImageId, msp.DarkLogoImageId ?? msp.LogoImageId, msp.SupportEmail, msp.SupportUrl, msp.SupportPhone, msp.FooterText);
    }

    /// <summary>The logo for the app bar. Light mode's app bar is the secondary colour, often dark, so the dark logo is
    /// used whenever the bar is dark, not only in dark mode.</summary>
    public static Guid? AppBarLogo(ResolvedBrand brand, bool darkMode) =>
        darkMode || PortalTheme.IsDark(brand.SecondaryColour) ? brand.DarkLogoImageId : brand.LogoImageId;
}

/// <summary>A portal page's title, suffixed with the brand's product name once it has loaded.</summary>
public static class PortalTitle
{
    public static string With(string title, ResolvedBrand? brand) => brand is null ? title : $"{title} - {brand.ProductName}";
}

/// <summary>Loads the resolved brand for a set of scoped Groups, for the portal layout and the staff previews.</summary>
public sealed class PortalBrandLoader(IDbContextFactory<DotMarcDbContext> dbFactory)
{
    public async Task<ResolvedBrand> LoadAsync(IReadOnlyCollection<int> groupIds, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var msp = await BrandingSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        var groups = await context.Groups.AsNoTracking().Where(group => groupIds.Contains(group.Id))
            .OrderBy(group => group.Name)
            .Select(group => new ScopedGroupBrand(group.Name)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return PortalBranding.Resolve(msp, groups);
    }
}
