using DotMarc.Portal;
using MudBlazor.Utilities;
using Xunit;

namespace DotMarc.Tests.Portal;

public sealed class PortalThemeTests
{
    [Fact]
    public void TheBrandColours_AreThePortalsPrimaryAndSecondary_InLightAndDark()
    {
        var theme = PortalTheme.For(new ResolvedBrand("Nova MSP", "Aurora Retail", "#0B5FFF", "#FF6B00", null, null, null, null, null, null));

        Assert.Equal("#0b5fffff", theme.PaletteLight.Primary.ToString(MudColorOutputFormats.HexA).ToLowerInvariant());
        Assert.Equal("#ff6b00ff", theme.PaletteDark.Secondary.ToString(MudColorOutputFormats.HexA).ToLowerInvariant());
    }

    [Fact]
    public void TheDefaultTheme_IsLeftAlone()
    {
        PortalTheme.For(new ResolvedBrand("Nova MSP", "Aurora Retail", "#0B5FFF", "#FF6B00", null, null, null, null, null, null));

        Assert.Equal("#e3594fff", PortalTheme.Default.PaletteLight.Primary.ToString(MudColorOutputFormats.HexA).ToLowerInvariant());
    }
}
