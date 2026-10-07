namespace DotMarc.Portal;

/// <summary>A Group's own portal branding, layered over the MSP's. Every field is optional; one left empty falls back to
/// the MSP brand. The row only exists while at least one field is set, and goes with its Group.</summary>
public sealed class GroupBranding
{
    public int GroupId { get; set; }

    /// <summary>The client's name as the portal heads it, in place of the Group's own name.</summary>
    public string? DisplayName { get; set; }

    public Guid? LogoImageId { get; set; }
    public Guid? DarkLogoImageId { get; set; }
    public string? PrimaryColour { get; set; }
    public string? SecondaryColour { get; set; }
}

public sealed record GroupBrandingInput(string? DisplayName, Guid? LogoImageId, Guid? DarkLogoImageId, string? PrimaryColour, string? SecondaryColour);
