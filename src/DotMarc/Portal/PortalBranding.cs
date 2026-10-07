using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Portal;

/// <summary>The brand a portal user sees, after layering their Groups over the MSP default.</summary>
public sealed record ResolvedBrand(string ProductName, string Heading, string PrimaryColour, string SecondaryColour,
    Guid? LogoImageId, Guid? DarkLogoImageId, string? SupportEmail, string? SupportUrl, string? SupportPhone, string? FooterText);

public sealed record ScopedGroupBrand(string GroupName, GroupBranding? Branding);

/// <summary>Works out the brand a portal user sees from the MSP default and their scoped Groups.</summary>
public static class PortalBranding
{
    /// <summary>A single branded Group among the user's Groups overrides the fields it sets; with two or more branded
    /// Groups none wins, so the MSP brand is used. The heading is the one Group's name (or its display name), or the
    /// product name when there are several.</summary>
    public static ResolvedBrand Resolve(BrandingSettings msp, IReadOnlyList<ScopedGroupBrand> scopedGroups)
    {
        var branded = scopedGroups.Where(group => group.Branding is not null).ToList();
        var groupBrand = branded.Count == 1 ? branded[0].Branding : null;
        var heading = scopedGroups.Count == 1 ? scopedGroups[0].Branding?.DisplayName ?? scopedGroups[0].GroupName : msp.ProductName;

        // A Group's own light logo pairs with its own dark logo, never with the MSP's.
        var logo = groupBrand?.LogoImageId ?? msp.LogoImageId;
        var darkLogo = groupBrand?.LogoImageId is not null
            ? groupBrand.DarkLogoImageId ?? groupBrand.LogoImageId
            : groupBrand?.DarkLogoImageId ?? msp.DarkLogoImageId ?? msp.LogoImageId;

        return new ResolvedBrand(msp.ProductName, heading,
            groupBrand?.PrimaryColour ?? msp.PrimaryColour, groupBrand?.SecondaryColour ?? msp.SecondaryColour,
            logo, darkLogo, msp.SupportEmail, msp.SupportUrl, msp.SupportPhone, msp.FooterText);
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
            .Select(group => new ScopedGroupBrand(group.Name, context.GroupBrandings.FirstOrDefault(branding => branding.GroupId == group.Id)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return PortalBranding.Resolve(msp, groups);
    }
}
