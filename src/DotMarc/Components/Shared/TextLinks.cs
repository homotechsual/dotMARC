using System.Text.RegularExpressions;

namespace DotMarc.Components.Shared;

public sealed record TextPart(string Text, bool IsLink);

/// <summary>Splits a message into plain text and https links, so a stored error message (such as a mailbox poll failure
/// with an admin consent link) can be shown with its link clickable. Only https links count, and a full stop or comma
/// straight after one is left as text.</summary>
public static partial class TextLinks
{
    public static IReadOnlyList<TextPart> Split(string text)
    {
        var parts = new List<TextPart>();
        var position = 0;
        foreach (Match match in HttpsLink().Matches(text))
        {
            if (match.Index > position)
            {
                parts.Add(new TextPart(text[position..match.Index], false));
            }

            parts.Add(new TextPart(match.Value, true));
            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            parts.Add(new TextPart(text[position..], false));
        }

        return parts;
    }

    [GeneratedRegex(@"https://[^\s""'<>]*[^\s""'<>.,;:)]")]
    private static partial Regex HttpsLink();
}
