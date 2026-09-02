namespace DotMarc.DnsPush;

public static class TlsrptRuaMerge
{
    public static string? TryMerge(string existingValue, string mailboxAddress)
    {
        if (!existingValue.StartsWith("v=TLSRPTv1", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var tags = existingValue.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        var ruaIndex = tags.FindIndex(tag => tag.StartsWith("rua=", StringComparison.OrdinalIgnoreCase));
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