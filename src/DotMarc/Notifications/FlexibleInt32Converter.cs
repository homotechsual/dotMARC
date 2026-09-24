using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.Notifications;

/// <summary>Reads an id that Halo may write as a number (<c>3</c>), a whole decimal (<c>3.0</c>) or a
/// string (<c>"3"</c>, <c>"3.0"</c>) - it does all three across its endpoints, and GET /api/Priority
/// returns its ids as strings. Anything that isn't a whole number in range is rejected rather than
/// truncated, since quietly turning <c>3.5</c> into <c>3</c> would send the wrong id back to Halo
/// later.</summary>
internal sealed class FlexibleInt32Converter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return ToWholeNumber(reader.GetDecimal());

            case JsonTokenType.String:
                var text = reader.GetString();
                const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
                if (decimal.TryParse(text, style, CultureInfo.InvariantCulture, out var parsed))
                {
                    return ToWholeNumber(parsed);
                }

                throw new JsonException($"Halo returned the id \"{Shorten(text)}\", which is not a number.");

            default:
                throw new JsonException($"Halo returned {reader.TokenType} where an id was expected.");
        }
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);

    private static int ToWholeNumber(decimal value) =>
        value == decimal.Truncate(value) && value is >= int.MinValue and <= int.MaxValue
            ? (int)value
            : throw new JsonException($"Halo returned the id {value.ToString(CultureInfo.InvariantCulture)}, which is not a whole number in range.");

    private static string Shorten(string? text) => text is null ? "" : text.Length <= 40 ? text : text[..40] + "...";
}
