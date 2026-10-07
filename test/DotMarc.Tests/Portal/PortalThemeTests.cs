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

    [Theory]
    [InlineData("#7A1FA2")] // purple: faint on the dark surface as it is
    [InlineData("#0B1B3F")] // navy: nearly invisible on it
    public void InDarkMode_ADarkPrimary_IsLightenedUntilItReadsOnTheDarkSurface(string primary)
    {
        var theme = PortalTheme.For(new ResolvedBrand("Nova MSP", "Aurora Retail", primary, "#FF6B00", null, null, null, null, null, null));

        var darkPrimary = theme.PaletteDark.Primary.ToString(MudColorOutputFormats.Hex);
        Assert.True(BrandColours.ContrastRatio(darkPrimary, PortalTheme.DarkSurface) >= BrandColours.MinimumTextContrast, $"{darkPrimary} on {PortalTheme.DarkSurface}");
        Assert.Equal(primary.ToLowerInvariant(), theme.PaletteLight.Primary.ToString(MudColorOutputFormats.Hex).ToLowerInvariant());
    }

    [Fact]
    public void InDarkMode_APrimaryThatAlreadyReads_IsKept()
    {
        var theme = PortalTheme.For(new ResolvedBrand("Nova MSP", "Aurora Retail", "#FFB74D", "#FF6B00", null, null, null, null, null, null));

        Assert.Equal("#ffb74d", theme.PaletteDark.Primary.ToString(MudColorOutputFormats.Hex).ToLowerInvariant());
    }

    [Fact]
    public void TheDefaultTheme_IsLeftAlone()
    {
        PortalTheme.For(new ResolvedBrand("Nova MSP", "Aurora Retail", "#0B5FFF", "#FF6B00", null, null, null, null, null, null));

        Assert.Equal("#e3594fff", PortalTheme.Default.PaletteLight.Primary.ToString(MudColorOutputFormats.HexA).ToLowerInvariant());
    }
}
