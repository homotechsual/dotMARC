using System.Text;
using DotMarc.Portal;
using Xunit;

namespace DotMarc.Tests.Portal;

public sealed class BrandingImagesTests
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0];

    private static byte[] Svg(string inner) => Encoding.UTF8.GetBytes($"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 10 10\">{inner}</svg>");

    [Fact]
    public void PngAndJpeg_AreRecognisedByTheirContent()
    {
        Assert.Equal("image/png", BrandingImages.Validate(PngHeader).ContentType);
        Assert.Equal("image/jpeg", BrandingImages.Validate(JpegHeader).ContentType);
    }

    [Fact]
    public void APlainSvg_IsAccepted()
    {
        var check = BrandingImages.Validate(Svg("<rect width=\"10\" height=\"10\" fill=\"#123456\"/><use href=\"#a\"/><image href=\"data:image/png;base64,AAAA\"/>"));

        Assert.Equal(("image/svg+xml", (string?)null), (check.ContentType, check.Problem));
    }

    [Fact]
    public void AnSvgUsingItsOwnGradient_IsAccepted()
    {
        var check = BrandingImages.Validate(Svg("<defs><linearGradient id=\"g\"/></defs><rect fill=\"url(#g)\" style=\"stroke: url( #g )\"/>"));

        Assert.Equal(("image/svg+xml", (string?)null), (check.ContentType, check.Problem));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<rect onload=\"alert(1)\"/>")]
    [InlineData("<foreignObject><div/></foreignObject>")]
    [InlineData("<image href=\"https://evil.example/x.png\"/>")]
    [InlineData("<a xlink:href=\"javascript:alert(1)\"><text>x</text></a>")]
    [InlineData("<use href=\"other.svg#a\"/>")]
    [InlineData("<a href=\"#x\"><set attributeName=\"href\" to=\"javascript:alert(1)\"/><text>x</text></a>")]
    [InlineData("<a href=\"#x\"><animate attributeName=\"xlink:href\" values=\"javascript:alert(1)\"/><text>x</text></a>")]
    [InlineData("<style>@import url(https://evil.example/x.css);</style>")]
    [InlineData("<rect style=\"fill: url(https://evil.example/x.svg#a)\"/>")]
    [InlineData("<rect fill=\"url('https://evil.example/x.svg#a')\"/>")]
    public void AnSvgThatCouldRunCodeOrFetchSomething_IsRefused(string inner)
    {
        var check = BrandingImages.Validate(Svg(inner));

        Assert.Null(check.ContentType);
        Assert.Equal("This SVG contains scripts or external links, so it can't be used.", check.Problem);
    }

    [Fact]
    public void AnSvgWithADoctype_IsRefused()
    {
        var withEntity = Encoding.UTF8.GetBytes("<!DOCTYPE svg [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><svg xmlns=\"http://www.w3.org/2000/svg\">&x;</svg>");

        Assert.NotNull(BrandingImages.Validate(withEntity).Problem);
    }

    [Fact]
    public void AnythingElse_OrTooLarge_IsRefused()
    {
        Assert.Equal("Logos must be PNG, JPEG or SVG, up to 512 KB.", BrandingImages.Validate(Encoding.UTF8.GetBytes("GIF89a")).Problem);

        var tooLarge = new byte[BrandingImages.MaximumBytes + 1];
        PngHeader.CopyTo(tooLarge, 0);
        Assert.Equal("Logos must be PNG, JPEG or SVG, up to 512 KB.", BrandingImages.Validate(tooLarge).Problem);
    }
}
