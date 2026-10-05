using System.Text.RegularExpressions;
using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>Tidies and checks a DKIM record pasted from a mail platform's admin console, which often wraps a long key
/// in quoted 255-character chunks or breaks it across lines.</summary>
public static partial class DkimRecordValue
{
    public static string Normalize(DkimRecordType type, string pasted)
    {
        var text = pasted.Trim();
        var chunks = QuotedChunk().Matches(text);
        if (chunks.Count > 0)
        {
            text = string.Concat(chunks.Select(chunk => chunk.Groups[1].Value.Replace("\\\"", "\"")));
        }

        if (type == DkimRecordType.Cname)
        {
            return Whitespace().Replace(text, "").TrimEnd('.').ToLowerInvariant();
        }

        // Line breaks inside the key are artefacts of the console's display; spaces between tags are collapsed.
        text = text.Replace("\r", "").Replace("\n", "");
        text = Whitespace().Replace(text, " ").Trim();
        return PublicKeyTag().Replace(text, match => "p=" + Whitespace().Replace(match.Groups[1].Value, ""));
    }

    /// <summary>Why a value can't be used, or null if it can.</summary>
    public static string? Validate(DkimRecordType type, string value) => type switch
    {
        DkimRecordType.Cname when !HostName().IsMatch(value) => "That isn't a host name. Paste the CNAME target the mail platform gives you.",
        DkimRecordType.Txt when string.IsNullOrEmpty(PublicKey(value)) => "A DKIM TXT value needs a p= tag with the public key.",
        _ => null
    };

    /// <summary>The p= tag's key with any whitespace removed, or null if there isn't one.</summary>
    public static string? PublicKey(string txt) =>
        txt.Split(';', StringSplitOptions.TrimEntries)
            .Where(tag => tag.StartsWith("p=", StringComparison.OrdinalIgnoreCase))
            .Select(tag => Whitespace().Replace(tag[2..], ""))
            .FirstOrDefault();

    /// <summary>Fastmail's DKIM records are CNAMEs whose targets follow from the selector and domain.</summary>
    public static string? FastmailTarget(string selector, string domainName) =>
        selector is "fm1" or "fm2" or "fm3" ? $"{selector}.{domainName}.dkim.fmhosted.com" : null;

    /// <summary>How a stored record reads in the audit log, such as "CNAME target.example".</summary>
    public static string Describe(DomainDkimRecord record) => $"{record.RecordType.ToString().ToUpperInvariant()} {record.Value}";

    [GeneratedRegex("\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex QuotedChunk();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"p=([^;]*)", RegexOptions.IgnoreCase)]
    private static partial Regex PublicKeyTag();

    // Labels of letters, digits, hyphens and underscores (DKIM targets contain _domainkey), at least two of them.
    [GeneratedRegex(@"^(?=.{1,253}$)[a-z0-9_-]{1,63}(\.[a-z0-9_-]{1,63})+$", RegexOptions.IgnoreCase)]
    private static partial Regex HostName();
}
