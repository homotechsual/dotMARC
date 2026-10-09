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

    [Theory]
    [InlineData("<html:iframe xmlns:html=\"http://www.w3.org/1999/xhtml\" src=\"https://evil.example\"/>")]
    [InlineData("<object xmlns=\"http://www.w3.org/1999/xhtml\" data=\"https://evil.example/x.swf\"/>")]
    [InlineData("<html:meta xmlns:html=\"http://www.w3.org/1999/xhtml\" http-equiv=\"refresh\" content=\"0;url=https://evil.example\"/>")]
    [InlineData("<style>rect { fill: \\75 rl(https://evil.example/x.svg#a); }</style>")]
    [InlineData("<style>@\\69mport 'https://evil.example/x.css';</style>")]
    [InlineData("<rect style=\"fill: \\75 rl(https://evil.example/x.svg#a)\"/>")]
    public void AnSvgSmugglingInHtmlOrEscapedCss_IsRefused(string inner)
    {
        Assert.Equal(BrandingImages.UnsafeSvg, BrandingImages.Validate(Svg(inner)).Problem);
    }

    [Fact]
    public void AnSvgWithAStylesheetInstruction_IsRefused()
    {
        var withStylesheet = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><?xml-stylesheet href=\"https://evil.example/x.css\"?><svg xmlns=\"http://www.w3.org/2000/svg\"/>");

        Assert.Equal(BrandingImages.UnsafeSvg, BrandingImages.Validate(withStylesheet).Problem);
    }

    [Fact]
    public void AnInkscapeLogo_WithItsMetadata_IsAccepted()
    {
        var inkscape = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="UTF-8" standalone="no"?>
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape"
                 xmlns:sodipodi="http://sodipodi.sourceforge.net/DTD/sodipodi-0.dtd" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                 xmlns:cc="http://creativecommons.org/ns#" xmlns:dc="http://purl.org/dc/elements/1.1/" viewBox="0 0 10 10">
              <sodipodi:namedview id="namedview1" inkscape:zoom="1"/>
              <metadata><rdf:RDF><cc:Work rdf:about=""><dc:format>image/svg+xml</dc:format><dc:type rdf:resource="http://purl.org/dc/dcmitype/StillImage"/></cc:Work></rdf:RDF></metadata>
              <g inkscape:label="Layer 1"><rect width="10" height="10" style="fill:#0b5fff;stroke:none"/></g>
            </svg>
            """);

        Assert.Equal(("image/svg+xml", (string?)null), (BrandingImages.Validate(inkscape).ContentType, BrandingImages.Validate(inkscape).Problem));
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
