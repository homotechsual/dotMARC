using System.Globalization;
using DotMarc.Data;
using DotMarc.Dns;

namespace DotMarc.Baselines;

/// <summary>Raises a DMARC record's policy to a target for the DNS push. It only ever strengthens p, sp and pct, and keeps
/// every other tag, the tag order and the record's spacing.</summary>
public static class DmarcPolicyRaise
{
    /// <summary>The policy for a record the push creates: p=none when there is no target, else the target's own text.</summary>
    public static string NewRecordPolicy(EffectiveDmarcTarget? target) =>
        target is null ? "p=none" : DmarcTargetText.Policy(target);

    /// <summary>Raises p and sp to at least the target and drops a pct below 100. Every repeated p and sp tag is raised
    /// and every partial pct removed, so the record reads the same whichever occurrence a receiver uses. A missing or
    /// invalid p is written as a valid one, because receivers ignore sp without it. An sp is added after p only when the
    /// record has none and the target's sp is stricter than the record's new p. Returns the record unchanged when it
    /// already meets the target.</summary>
    public static string Apply(string record, EffectiveDmarcTarget target)
    {
        var current = DmarcPolicyTags.Parse(record);
        if (current is null)
        {
            return record;
        }

        var parts = record.Split(';').ToList();
        var newPolicy = (DmarcPolicyLevel)Math.Max((int)current.Policy, (int)target.Policy);
        var newSubdomainPolicy = (DmarcPolicyLevel)Math.Max((int)current.SubdomainPolicy, (int)target.SubdomainPolicy);
        var changed = false;
        var hasPolicy = false;
        var hasSubdomainPolicy = false;

        for (var index = parts.Count - 1; index >= 0; index--)
        {
            var tagName = TagName(parts[index]);
            if (tagName is null)
            {
                continue;
            }

            if (string.Equals(tagName, "p", StringComparison.OrdinalIgnoreCase))
            {
                hasPolicy = true;
                var level = ParseLevel(TagValue(parts[index]));
                if (level is null || level < newPolicy)
                {
                    parts[index] = WithValue(parts[index], Name(newPolicy));
                    changed = true;
                }
            }
            else if (string.Equals(tagName, "sp", StringComparison.OrdinalIgnoreCase))
            {
                hasSubdomainPolicy = true;
                // An invalid sp is ignored by receivers, so it effectively is p.
                var level = ParseLevel(TagValue(parts[index])) ?? newPolicy;
                if (level < newSubdomainPolicy)
                {
                    parts[index] = WithValue(parts[index], Name(newSubdomainPolicy));
                    changed = true;
                }
            }
            else if (string.Equals(tagName, "pct", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(TagValue(parts[index]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent)
                     && percent is >= 0 and < 100)
            {
                parts.RemoveAt(index);
                changed = true;
            }
        }

        if (!hasPolicy)
        {
            // No p tag at all: put one straight after the version tag.
            parts.Insert(1, $" p={Name(newPolicy)}");
            changed = true;
        }

        if (!hasSubdomainPolicy && target.SubdomainPolicy > newPolicy)
        {
            // The implicit sp follows p, so an explicit one is only needed when the target wants sp stricter than p.
            parts.Insert(IndexOfTag(parts, "p") + 1, $" sp={Name(target.SubdomainPolicy)}");
            changed = true;
        }

        return changed ? string.Join(';', parts) : record;
    }

    private static string? TagName(string part)
    {
        var separator = part.IndexOf('=');
        return separator > 0 ? part[..separator].Trim() : null;
    }

    private static string TagValue(string part) => part[(part.IndexOf('=') + 1)..].Trim();

    private static DmarcPolicyLevel? ParseLevel(string text) => text.ToLowerInvariant() switch
    {
        "none" => DmarcPolicyLevel.None,
        "quarantine" => DmarcPolicyLevel.Quarantine,
        "reject" => DmarcPolicyLevel.Reject,
        _ => null
    };

    private static int IndexOfTag(List<string> parts, string tagName)
    {
        for (var index = 0; index < parts.Count; index++)
        {
            if (string.Equals(TagName(parts[index]), tagName, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static string WithValue(string part, string newValue)
    {
        var separator = part.IndexOf('=');
        var value = part[(separator + 1)..];
        var leading = value.Length - value.TrimStart().Length;
        var trailing = value.Length - value.TrimEnd().Length;
        return string.Concat(part.AsSpan(0, separator + 1), value.AsSpan(0, leading), newValue, trailing > 0 ? value[^trailing..] : "");
    }

    private static string Name(DmarcPolicyLevel level) => level.ToString().ToLower(CultureInfo.InvariantCulture);
}
