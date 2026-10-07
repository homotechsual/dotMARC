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
}
