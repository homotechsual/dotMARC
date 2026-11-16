using DotMarc.Dns;

namespace DotMarc.DnsPush;

/// <summary>Replaces (or appends) the rua= tag in an existing _dmarc TXT record's value, leaving
/// every other tag untouched - pushing a fix for DmarcCheckStatus.Misconfigured must not silently
/// discard tags (sp=, pct=, adkim=, etc.) a customer set on purpose.</summary>
public static class DmarcRuaMerge
{
    /// <summary>The rua= tag for a record dotMARC writes from scratch: dotMARC's mailbox, then the admin's additional
    /// address if one is set.</summary>
    public static string RuaTag(string mailboxAddress, string? additionalAddress = null) =>
        BuildRuaTag(mailboxAddress, additionalAddress, []);

    /// <summary>Returns the merged value, or null if <paramref name="existingValue"/> doesn't even
    /// start with "v=DMARC1" - not safe to merge into; the caller should offer a full-replacement
    /// warning instead.</summary>
    /// <param name="additionalAddress">The admin's extra destination, written after dotMARC's mailbox.</param>
    /// <param name="keepOtherAddresses">Keep addresses already in rua= (normalising a missing mailto:) in the room left
    /// after those two. Off, the tag is dotMARC's mailbox and the additional address only.</param>
    public static string? TryMerge(string existingValue, string mailboxAddress, string? additionalAddress = null, bool keepOtherAddresses = true)
    {
        if (!existingValue.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var tags = existingValue
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        var ruaIndex = tags.FindIndex(t => t.StartsWith("rua=", StringComparison.OrdinalIgnoreCase));
        var existingEntries = keepOtherAddresses ? DotMarc.Dns.DmarcRua.Parse(existingValue) : [];
        var ruaTag = BuildRuaTag(mailboxAddress, additionalAddress, existingEntries);

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

    /// <summary>dotMARC's mailbox first, then the additional address, then the existing addresses in their original order,
    /// each address once, cut at the most a record can list before receivers may ignore the rest.</summary>
    private static string BuildRuaTag(string mailboxAddress, string? additionalAddress, IEnumerable<DmarcRuaEntry> existingEntries)
    {
        var entries = new List<string> { $"mailto:{mailboxAddress}" };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mailboxAddress };

        if (!string.IsNullOrWhiteSpace(additionalAddress) && seen.Add(additionalAddress))
        {
            entries.Add($"mailto:{additionalAddress}");
        }

        foreach (var entry in existingEntries)
        {
            if (seen.Add(entry.Address))
            {
                entries.Add(entry.ToUri());
            }
        }

        return "rua=" + string.Join(",", entries.Take(DmarcDnsChecker.MaxReportAddresses));
    }
}
