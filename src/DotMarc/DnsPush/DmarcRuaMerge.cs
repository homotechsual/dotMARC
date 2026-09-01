namespace DotMarc.DnsPush;

/// <summary>Replaces (or appends) the rua= tag in an existing _dmarc TXT record's value, leaving
/// every other tag untouched — pushing a fix for DmarcCheckStatus.Misconfigured must not silently
/// discard tags (sp=, pct=, adkim=, etc.) a customer set on purpose.</summary>
public static class DmarcRuaMerge
{
    /// <summary>Returns the merged value, or null if <paramref name="existingValue"/> doesn't even
    /// start with "v=DMARC1" — not safe to merge into; the caller should offer a full-replacement
    /// warning instead.</summary>
    public static string? TryMerge(string existingValue, string mailboxAddress)
    {
        if (!existingValue.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var tags = existingValue
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        var ruaIndex = tags.FindIndex(t => t.StartsWith("rua=", StringComparison.OrdinalIgnoreCase));
        var ruaTag = $"rua=mailto:{mailboxAddress}";

        if (ruaIndex >= 0)
        {
            tags[ruaIndex] = ruaTag;
        }
        else
        {
            tags.Add(ruaTag);
        }

        return string.Join("; ", tags);
    }
}
