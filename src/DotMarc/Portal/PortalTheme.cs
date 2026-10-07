using MudBlazor;

namespace DotMarc.Portal;

/// <summary>dotMARC's look, shared by the app and the client portal. The portal swaps in the brand's colours.</summary>
public static class PortalTheme
{
    // Homotechsual brand palette - matches GCT's MainLayout.razor exactly, so the two
    // products read as one family rather than each inventing its own look.
    public static MudTheme Default => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#e3594f",
            PrimaryLighten = "#efaaa5",
            PrimaryDarken = "#c9443a",
            PrimaryContrastText = "#fcfcfc",
            Secondary = "#263141",
            SecondaryLighten = "#3d4f63",
            SecondaryDarken = "#1a2230",
            SecondaryContrastText = "#fcfcfc",
            Info = "#263141",
            InfoContrastText = "#fcfcfc",
            TextSecondary = "#616161",
            SuccessContrastText = "rgba(0,0,0,0.87)",
            ErrorContrastText = "rgba(0,0,0,0.87)",
            AppbarBackground = "#263141",
            AppbarText = "#fcfcfc",
            DrawerBackground = "#263141",
            DrawerText = "#fcfcfc",
            DrawerIcon = "#fcfcfc",
        },
        PaletteDark = new PaletteDark
        {
            Background = "#111827",
            Surface = "#1e2a3a",
            AppbarBackground = "#0f1724",
            DrawerBackground = "#0f1724",
            Primary = "#ef8b86",
            PrimaryLighten = "#fad0cc",
            PrimaryDarken = "#e3594f",
            PrimaryContrastText = "rgba(0,0,0,0.87)",
            Secondary = "#3d4f63",
            SecondaryContrastText = "#fcfcfc",
            AppbarText = "#fcfcfc",
            DrawerText = "#fcfcfc",
            DrawerIcon = "#fcfcfc",
            /* MudBlazor's default Info blue reads as barely-distinguishable "blue on blue" against
               this brand's navy dark-mode surface; a lighter, more saturated sky blue keeps enough
               contrast against both the surface and its own translucent alert background. */
            Info = "#5ec8f2",
            InfoContrastText = "rgba(0,0,0,0.87)",
            TextPrimary = "#e8eaf0",
            TextSecondary = "#adb5c7",
            LinesDefault = "rgba(255,255,255,0.08)",
            SuccessContrastText = "rgba(0,0,0,0.87)",
            ErrorContrastText = "rgba(0,0,0,0.87)",
            WarningContrastText = "rgba(0,0,0,0.87)",
        }
    };

    private const string LightText = "#FCFCFC";
    private const string DarkText = "#000000";

    /// <summary>The default look in the brand's colours: primary and secondary in both palettes, and the light app bar in
    /// the secondary colour as dotMARC's own is. Dark mode keeps its own app bar, so the page stays dark.</summary>
    public static MudTheme For(ResolvedBrand brand)
    {
        var theme = Default;
        var primary = new MudBlazor.Utilities.MudColor(brand.PrimaryColour);
        var secondary = new MudBlazor.Utilities.MudColor(brand.SecondaryColour);

        theme.PaletteLight.Primary = primary;
        theme.PaletteLight.PrimaryLighten = primary.ColorLighten(0.2).ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex);
        theme.PaletteLight.PrimaryDarken = primary.ColorDarken(0.1).ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex);
        theme.PaletteLight.PrimaryContrastText = ReadableTextOn(brand.PrimaryColour);
        theme.PaletteLight.Secondary = secondary;
        theme.PaletteLight.SecondaryLighten = secondary.ColorLighten(0.1).ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex);
        theme.PaletteLight.SecondaryDarken = secondary.ColorDarken(0.1).ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex);
        theme.PaletteLight.SecondaryContrastText = ReadableTextOn(brand.SecondaryColour);
        theme.PaletteLight.Info = secondary;
        theme.PaletteLight.InfoContrastText = ReadableTextOn(brand.SecondaryColour);
        theme.PaletteLight.AppbarBackground = secondary;
        theme.PaletteLight.AppbarText = ReadableTextOn(brand.SecondaryColour);

        theme.PaletteDark.Primary = primary;
        theme.PaletteDark.PrimaryLighten = primary.ColorLighten(0.2).ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex);
        theme.PaletteDark.PrimaryDarken = primary.ColorDarken(0.1).ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex);
        theme.PaletteDark.PrimaryContrastText = ReadableTextOn(brand.PrimaryColour);
        theme.PaletteDark.Secondary = secondary;
        theme.PaletteDark.SecondaryContrastText = ReadableTextOn(brand.SecondaryColour);
        return theme;
    }

    /// <summary>Whether the colour is dark enough that light text reads better on it.</summary>
    public static bool IsDark(string colour) => ReadableTextOn(colour) == LightText;

    /// <summary>Near-white or black, whichever reads better on the colour.</summary>
    public static string ReadableTextOn(string colour) =>
        BrandColours.ContrastRatio(colour, LightText) >= BrandColours.ContrastRatio(colour, DarkText) ? LightText : DarkText;
}
