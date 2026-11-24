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

    /// <summary>Raises p and sp to at least the target and drops a pct below 100. An sp is added after p only when the
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
        var policyIndex = IndexOfTag(parts, "p");
        var subdomainIndex = IndexOfTag(parts, "sp");
        var percentIndex = IndexOfTag(parts, "pct");

        var newPolicy = (DmarcPolicyLevel)Math.Max((int)current.Policy, (int)target.Policy);
        var newSubdomainPolicy = (DmarcPolicyLevel)Math.Max((int)current.SubdomainPolicy, (int)target.SubdomainPolicy);
        var changed = false;

        // Rewrite values in place first (indexes stay valid), then remove pct, then insert, adjusting indexes as parts move.
        if (subdomainIndex >= 0 && newSubdomainPolicy > current.SubdomainPolicy)
        {
            parts[subdomainIndex] = WithValue(parts[subdomainIndex], Name(newSubdomainPolicy));
            changed = true;
        }

        if (policyIndex >= 0 && newPolicy > current.Policy)
        {
            parts[policyIndex] = WithValue(parts[policyIndex], Name(newPolicy));
            changed = true;
        }

        if (percentIndex >= 0 && current.Percent < 100)
        {
            parts.RemoveAt(percentIndex);
            if (policyIndex > percentIndex) policyIndex--;
            if (subdomainIndex > percentIndex) subdomainIndex--;
            changed = true;
        }

        if (policyIndex < 0 && newPolicy > current.Policy)
        {
            // No p tag at all: put one straight after the version tag.
            parts.Insert(1, $" p={Name(newPolicy)}");
            policyIndex = 1;
            if (subdomainIndex >= 1) subdomainIndex++;
            changed = true;
        }

        if (subdomainIndex < 0 && target.SubdomainPolicy > newPolicy)
        {
            // The implicit sp follows p, so an explicit one is only needed when the target wants sp stricter than p.
            parts.Insert(Math.Max(policyIndex, 0) + 1, $" sp={Name(target.SubdomainPolicy)}");
            changed = true;
        }

        return changed ? string.Join(';', parts) : record;
    }

    private static int IndexOfTag(List<string> parts, string tagName)
    {
        for (var index = 0; index < parts.Count; index++)
        {
            var separator = parts[index].IndexOf('=');
            if (separator > 0 && string.Equals(parts[index][..separator].Trim(), tagName, StringComparison.OrdinalIgnoreCase))
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
