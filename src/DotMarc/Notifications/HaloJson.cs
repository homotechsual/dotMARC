using System.Globalization;
using System.Text.Json;

namespace DotMarc.Notifications;

internal static class HaloJson
{
    /// <summary>Reads an id Halo may write as a number (<c>3</c>), a whole decimal (<c>3.0</c>) or a string
    /// (<c>"3"</c>). Anything else, including a GUID or a fraction, is refused rather than truncated.</summary>
    public static bool TryGetWholeNumber(JsonElement element, out int value)
    {
        value = 0;
        decimal number = 0;
        const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint
            | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;

        var parsed = element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDecimal(out number),
            JsonValueKind.String => decimal.TryParse(element.GetString(), style, CultureInfo.InvariantCulture, out number),
            _ => false
        };

        if (!parsed || number != decimal.Truncate(number) || number < int.MinValue || number > int.MaxValue)
        {
            return false;
        }

        value = (int)number;
        return true;
    }

    /// <summary>Finds a property by name ignoring case, since Halo isn't consistent about it.</summary>
    public static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
