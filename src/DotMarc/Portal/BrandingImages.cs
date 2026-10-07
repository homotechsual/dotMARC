using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DotMarc.Portal;

public sealed record BrandingImageCheck(string? ContentType, string? Problem);

/// <summary>Decides whether uploaded bytes can be a logo. The type comes from the content, never the file name. SVGs are
/// parsed and refused if they could run code or fetch anything, because the portal is shown to people outside the MSP.</summary>
public static partial class BrandingImages
{
    public const int MaximumBytes = 512 * 1024;
    public const string WrongTypeOrSize = "Logos must be PNG, JPEG or SVG, up to 512 KB.";
    public const string UnsafeSvg = "This SVG contains scripts or external links, so it can't be used.";

    public static BrandingImageCheck Validate(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumBytes)
        {
            return new BrandingImageCheck(null, WrongTypeOrSize);
        }

        if (bytes.AsSpan().StartsWith((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return new BrandingImageCheck("image/png", null);
        }

        if (bytes.AsSpan().StartsWith((byte[])[0xFF, 0xD8, 0xFF]))
        {
            return new BrandingImageCheck("image/jpeg", null);
        }

        return ValidateSvg(bytes);
    }

    private static BrandingImageCheck ValidateSvg(byte[] bytes)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            // Not XML at all is the wrong type; XML with a DTD is refused as unsafe.
            return Encoding.UTF8.GetString(bytes).Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                ? new BrandingImageCheck(null, UnsafeSvg)
                : new BrandingImageCheck(null, WrongTypeOrSize);
        }

        if (document.Root?.Name.LocalName != "svg")
        {
            return new BrandingImageCheck(null, WrongTypeOrSize);
        }

        foreach (var element in document.Root.DescendantsAndSelf())
        {
            if (element.Name.LocalName is "script" or "foreignObject")
            {
                return new BrandingImageCheck(null, UnsafeSvg);
            }

            // An animation can rewrite a link after the checks below have passed it.
            if (element.Name.LocalName is "set" or "animate" && IsHrefName((string?)element.Attribute("attributeName")))
            {
                return new BrandingImageCheck(null, UnsafeSvg);
            }

            if (element.Name.LocalName == "style" && FetchesSomething(element.Value))
            {
                return new BrandingImageCheck(null, UnsafeSvg);
            }

            foreach (var attribute in element.Attributes())
            {
                // LocalName drops the prefix, so this covers xlink:href as well as href.
                var name = attribute.Name.LocalName;
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || (name == "href" && !(attribute.Value.StartsWith('#') || attribute.Value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)))
                    || FetchesSomething(attribute.Value))
                {
                    return new BrandingImageCheck(null, UnsafeSvg);
                }
            }
        }

        return new BrandingImageCheck("image/svg+xml", null);
    }

    private static bool IsHrefName(string? attributeName) =>
        attributeName is not null && (attributeName == "href" || attributeName.EndsWith(":href", StringComparison.Ordinal));

    // CSS can fetch through @import or url(...); only references to the SVG's own fragments (url(#gradient)) are allowed.
    private static bool FetchesSomething(string css) =>
        css.Contains("@import", StringComparison.OrdinalIgnoreCase)
        || CssUrl().Matches(css).Any(match => !match.Groups["target"].Value.StartsWith('#'));

    [GeneratedRegex(@"url\(\s*['""]?\s*(?<target>[^)'""\s]*)", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrl();
}
