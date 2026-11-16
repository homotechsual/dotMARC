namespace DotMarc.Dns;

/// <summary>One address from a DMARC record's rua= tag. <paramref name="SizeSuffix"/> is the optional RFC 7489 §6.3
/// "!10m" maximum report size, kept so a push that preserves the entry doesn't lose it. <paramref name="HasMailtoScheme"/>
/// is false for a bare address, which RFC 7489 §6.2 does not allow (rua= is a list of URIs) and receivers ignore.</summary>
public sealed record DmarcRuaEntry(string Address, string SizeSuffix, bool HasMailtoScheme)
{
    /// <summary>The entry as it should be written back to DNS, always with the mailto: scheme.</summary>
    public string ToUri() => $"mailto:{Address}{SizeSuffix}";
}

/// <summary>Reads the rua= tag of a DMARC record. Shared by the DNS check and the DNS push so both agree on what a
/// record lists.</summary>
public static class DmarcRua
{
    public static List<DmarcRuaEntry> Parse(string record)
    {
        var ruaTag = record.Split(';')
            .Select(part => part.Trim())
            .FirstOrDefault(part => part.StartsWith("rua=", StringComparison.OrdinalIgnoreCase));

        if (ruaTag is null)
        {
            return [];
        }

        return ruaTag["rua=".Length..]
            .Split(',')
            .Select(uri => uri.Trim())
            // An entry with no scheme at all (a bare address, "mailto:" forgotten) is still an address the owner
            // meant to list; other schemes (https:) are not addresses and are ignored.
            .Where(uri => uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || (uri.Contains('@') && !uri.Contains(':')))
            .Select(uri =>
            {
                var hasScheme = uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
                var rest = hasScheme ? uri["mailto:".Length..] : uri;
                var bang = rest.IndexOf('!');
                return bang < 0
                    ? new DmarcRuaEntry(rest, "", hasScheme)
                    : new DmarcRuaEntry(rest[..bang], rest[bang..], hasScheme);
            })
            .ToList();
    }
}
