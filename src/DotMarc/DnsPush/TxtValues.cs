using System.Text;
using DotMarc.Dns;

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

    /// <summary>Plans replacing values in the TXT set at a name. Values are matched by their trimmed text, but the
    /// items kept are each provider's originals, untouched, so a push never rewrites a value it isn't changing. The
    /// plan has a <see cref="TxtSetEdit{T}.Problem"/> (and must not be applied) if a value to remove is no longer
    /// there, or if adding an SPF record would leave the name with anything but exactly one, the error receivers
    /// treat as no SPF at all.</summary>
    public static TxtSetEdit<T> PlanReplace<T>(IReadOnlyList<T> current, Func<T, string> valueOf, IReadOnlyList<string> remove, string add)
    {
        var addKey = add.Trim();
        var removeKeys = remove.Select(value => value.Trim()).ToHashSet(StringComparer.Ordinal);
        var currentKeys = current.Select(item => valueOf(item).Trim()).ToList();
        var missing = remove.Where(value => !currentKeys.Contains(value.Trim(), StringComparer.Ordinal)).ToList();

        var removed = current.Where(item => removeKeys.Contains(valueOf(item).Trim()) && valueOf(item).Trim() != addKey).ToList();
        var kept = current.Except(removed).ToList();
        var addNew = !kept.Any(item => valueOf(item).Trim() == addKey);

        string? problem = null;
        if (missing.Count > 0)
        {
            problem = "The TXT records at this name changed since this push started, so nothing was changed. Try again.";
        }
        else if (SpfRecord.IsSpf(addKey))
        {
            var spfCount = kept.Count(item => SpfRecord.IsSpf(valueOf(item).Trim())) + (addNew ? 1 : 0);
            if (spfCount != 1)
            {
                problem = $"This push would leave {spfCount} SPF records at this name, and receivers treat more than one as no SPF at all, so nothing was changed. Reopen the SPF editor to start from what's there now.";
            }
        }

        return new TxtSetEdit<T>(kept, removed, addNew, missing, problem);
    }
}

/// <summary>A planned change to a TXT set: the items to keep and remove (each provider's own originals), whether the
/// new value still needs adding, and why the plan mustn't be applied, if it mustn't.</summary>
public sealed record TxtSetEdit<T>(IReadOnlyList<T> Kept, IReadOnlyList<T> Removed, bool AddNew, IReadOnlyList<string> Missing, string? Problem);
