using System.Globalization;
using System.Text.RegularExpressions;

namespace DotMarc.Portal;

public static partial class BrandColours
{
    /// <summary>WCAG AA for normal text.</summary>
    public const double MinimumTextContrast = 4.5;

    public static bool IsValid(string? value) => value is not null && HexColour().IsMatch(value);

    /// <summary>The WCAG contrast ratio between two #RRGGBB colours, from 1 (none) to 21 (black on white).</summary>
    public static double ContrastRatio(string first, string second)
    {
        var lighter = Math.Max(Luminance(first), Luminance(second));
        var darker = Math.Min(Luminance(first), Luminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(string colour)
    {
        double Channel(int offset)
        {
            var value = int.Parse(colour.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColour();
}
