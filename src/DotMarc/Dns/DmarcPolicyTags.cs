using System.Globalization;
using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>A DMARC record's policy tags, with sp and pct filled in from their defaults (sp is p; pct is 100). Also
/// the format the DMARC policy weakened alert stores its accepted policy in.</summary>
public sealed record DmarcPolicyTags(DmarcPolicyLevel Policy, DmarcPolicyLevel SubdomainPolicy, int Percent)
{
    /// <summary>Reads p, sp and pct from a DMARC record, or from a baseline written by <see cref="Format"/>. Null only
    /// when there's no text; a missing or invalid p reads as none. Tag names and values ignore case and spaces, and the first
    /// of a repeated tag wins.</summary>
    public static DmarcPolicyTags? Parse(string? record)
    {
        if (string.IsNullOrWhiteSpace(record))
        {
            return null;
        }

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in record.Split(';'))
        {
            var separator = part.IndexOf('=');
            if (separator > 0)
            {
                tags.TryAdd(part[..separator].Trim(), part[(separator + 1)..].Trim());
            }
        }

        // A missing or invalid p is read as none, as receivers treat it (RFC 7489 6.6.3), so a typo that turns a
        // reject policy into nothing is caught as a weakened policy rather than skipped.
        var policy = tags.TryGetValue("p", out var policyText) ? ParseLevel(policyText) ?? DmarcPolicyLevel.None : DmarcPolicyLevel.None;

        var subdomainPolicy = tags.TryGetValue("sp", out var subdomainText) ? ParseLevel(subdomainText) ?? policy : policy;
        var percent = tags.TryGetValue("pct", out var percentText)
                      && int.TryParse(percentText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPercent)
                      && parsedPercent is >= 0 and <= 100
            ? parsedPercent
            : 100;
        return new DmarcPolicyTags(policy, subdomainPolicy, percent);
    }

    /// <summary>A domain's policy as the DMARC check last stored it, or null if it had no DMARC record.</summary>
    public static DmarcPolicyTags? Of(Domain domain) =>
        domain.DmarcPolicy is { } policy
            ? new DmarcPolicyTags(policy, domain.DmarcSubdomainPolicy ?? policy, domain.DmarcPercent ?? 100)
            : null;

    public string Format() => $"p={Name(Policy)}; sp={Name(SubdomainPolicy)}; pct={Percent.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>True if any of p, sp or pct is lower than in <paramref name="other"/>.</summary>
    public bool IsWeakerThan(DmarcPolicyTags other) =>
        Policy < other.Policy || SubdomainPolicy < other.SubdomainPolicy || Percent < other.Percent;

    private static DmarcPolicyLevel? ParseLevel(string text) => text.ToLowerInvariant() switch
    {
        "none" => DmarcPolicyLevel.None,
        "quarantine" => DmarcPolicyLevel.Quarantine,
        "reject" => DmarcPolicyLevel.Reject,
        _ => null
    };

    private static string Name(DmarcPolicyLevel level) => level.ToString().ToLowerInvariant();
}
