using System.Text;

namespace DotMarc.DnsPush;

/// <summary>TXT values as DNS stores them: one value is one or more strings of at most 255 characters, which Cloudflare
/// and Google take as zone-file text (<c>"a" "b"</c>) and Azure as a list. DKIM keys are longer than 255.</summary>
public static class TxtValues
{
    public const int MaxStringLength = 255;

    public static IReadOnlyList<string> Split(string value) =>
        value.Length <= MaxStringLength ? [value] : value.Chunk(MaxStringLength).Select(chunk => new string(chunk)).ToList();

    public static string ToQuotedText(string value) =>
        string.Join(' ', Split(value).Select(chunk => $"\"{chunk.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""));

    public static string FromQuotedText(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('"'))
        {
            return trimmed;
        }

        var value = new StringBuilder();
        var inString = false;
        for (var index = 0; index < trimmed.Length; index++)
        {
            var character = trimmed[index];
            if (character == '"')
            {
                inString = !inString;
            }
            else if (inString && character == '\\' && index + 1 < trimmed.Length)
            {
                value.Append(trimmed[++index]);
            }
            else if (inString)
            {
                value.Append(character);
            }
        }

        return value.ToString();
    }

    /// <summary>The values at a name after removing <paramref name="remove"/> and adding <paramref name="add"/>, and
    /// any of <paramref name="remove"/> that weren't there (a sign the name changed since the push was planned).</summary>
    public static (List<string> Values, IReadOnlyList<string> Missing) ReplaceValues(IEnumerable<string> current, IReadOnlyList<string> remove, string add)
    {
        var currentValues = current.Select(value => value.Trim()).ToList();
        var missing = remove.Where(value => !currentValues.Contains(value.Trim(), StringComparer.Ordinal)).ToList();
        var values = currentValues.Where(value => !remove.Contains(value, StringComparer.Ordinal)).ToList();
        if (!values.Contains(add, StringComparer.Ordinal))
        {
            values.Add(add);
        }

        return (values, missing);
    }
}
