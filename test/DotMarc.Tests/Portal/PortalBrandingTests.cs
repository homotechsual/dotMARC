using DotMarc.Portal;
using Xunit;

namespace DotMarc.Tests.Portal;

public sealed class PortalBrandingTests
{
    private static BrandingSettings Msp() => new()
    {
        ProductName = "Nova MSP", PrimaryColour = "#0B5FFF", SecondaryColour = "#FF6B00",
        LogoImageId = Guid.Parse("11111111-1111-1111-1111-111111111111"), SupportEmail = "help@nova-msp.example",
    };

    [Fact]
    public void OneScopedGroup_HeadsThePageWithItsName_InTheMspBrand()
    {
        var brand = PortalBranding.Resolve(Msp(), [new ScopedGroupBrand("Aurora Retail", null)]);

        Assert.Equal(("Nova MSP", "Aurora Retail", "#0B5FFF", "help@nova-msp.example"), (brand.ProductName, brand.Heading, brand.PrimaryColour, brand.SupportEmail));
    }

    [Fact]
    public void SeveralScopedGroups_AreHeadedWithTheProductName()
    {
        var brand = PortalBranding.Resolve(Msp(), [new ScopedGroupBrand("Aurora Retail", null), new ScopedGroupBrand("Aurora Online", null)]);

        Assert.Equal("Nova MSP", brand.Heading);
    }

    [Theory]
    [InlineData("#0B1B3F", false, "dark")] // a dark app bar in light mode still needs the logo made for dark backgrounds
    [InlineData("#F4F6FA", false, "light")]
    [InlineData("#F4F6FA", true, "dark")] // dark mode keeps its own dark app bar
    public void TheAppBarLogo_SuitsTheAppBarsBackground(string secondaryColour, bool darkMode, string expected)
    {
        var lightLogo = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var darkLogo = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var brand = new ResolvedBrand("Nova MSP", "Aurora Retail", "#0B5FFF", secondaryColour, lightLogo, darkLogo, null, null, null, null);

        Assert.Equal(expected == "dark" ? darkLogo : lightLogo, PortalBranding.AppBarLogo(brand, darkMode));
    }

    [Fact]
    public void OneBrandedGroup_OverridesTheFieldsItSets_AndKeepsTheRest()
    {
        var aurora = new GroupBranding { DisplayName = "Aurora Retail Ltd", PrimaryColour = "#7A1FA2" };

        var brand = PortalBranding.Resolve(Msp(), [new ScopedGroupBrand("Aurora Retail", aurora)]);

        Assert.Equal(("Aurora Retail Ltd", "#7A1FA2", "#FF6B00", "Nova MSP"), (brand.Heading, brand.PrimaryColour, brand.SecondaryColour, brand.ProductName));
        Assert.Equal(Msp().LogoImageId, brand.LogoImageId);
    }

    [Fact]
    public void TwoBrandedGroups_UseTheMspBrand_AndTheProductName()
    {
        var brand = PortalBranding.Resolve(Msp(),
        [
            new ScopedGroupBrand("Aurora Retail", new GroupBranding { PrimaryColour = "#7A1FA2" }),
            new ScopedGroupBrand("Aurora Online", new GroupBranding { PrimaryColour = "#00897B" }),
        ]);

        Assert.Equal(("Nova MSP", "#0B5FFF"), (brand.Heading, brand.PrimaryColour));
    }

    [Fact]
    public void OneBrandedGroupAmongSeveral_IsUsed_ButTheHeadingIsTheProductName()
    {
        var brand = PortalBranding.Resolve(Msp(),
        [
            new ScopedGroupBrand("Aurora Retail", new GroupBranding { PrimaryColour = "#7A1FA2" }),
            new ScopedGroupBrand("Aurora Online", null),
        ]);

        Assert.Equal(("Nova MSP", "#7A1FA2"), (brand.Heading, brand.PrimaryColour));
    }

    [Fact]
    public void AGroupLogo_WithoutADarkOne_IsUsedInDarkModeToo()
    {
        var logo = Guid.NewGuid();

        var brand = PortalBranding.Resolve(Msp(), [new ScopedGroupBrand("Aurora Retail", new GroupBranding { LogoImageId = logo })]);

        Assert.Equal((logo, logo), (brand.LogoImageId, brand.DarkLogoImageId));
    }

    [Fact]
    public void TheDarkLogo_FallsBackToTheLightOne()
    {
        var brand = PortalBranding.Resolve(Msp(), [new ScopedGroupBrand("Aurora Retail", null)]);

        Assert.Equal(brand.LogoImageId, brand.DarkLogoImageId);
    }
}
