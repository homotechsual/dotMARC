namespace DotMarc.Portal;

/// <summary>Singleton row (seeded <c>Id = 1</c>) holding the MSP-wide brand the client portal shows: the product name,
/// colours, logos and support contact. A Group's own branding, where set, layers over it.</summary>
public sealed class BrandingSettings
{
    /// <summary>dotMARC's own primary and secondary colours, the same as <see cref="PortalTheme.Default"/>'s light palette.</summary>
    public const string DefaultPrimaryColour = "#E3594F";
    public const string DefaultSecondaryColour = "#263141";

    public int Id { get; set; }
    public string ProductName { get; set; } = "dotMARC";
    public string PrimaryColour { get; set; } = DefaultPrimaryColour;
    public string SecondaryColour { get; set; } = DefaultSecondaryColour;

    /// <summary>The logo for light backgrounds. A plain id, not a foreign key, so removing an image never cascades here.</summary>
    public Guid? LogoImageId { get; set; }

    /// <summary>The logo for dark backgrounds; the light logo is used when this isn't set.</summary>
    public Guid? DarkLogoImageId { get; set; }

    public string? SupportEmail { get; set; }
    public string? SupportUrl { get; set; }
    public string? SupportPhone { get; set; }
    public string? FooterText { get; set; }
}
