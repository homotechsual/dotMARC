using DotMarc.Data;
using DotMarc.Dns;

namespace DotMarc.Baselines;

/// <summary>Plain-language descriptions of an effective DMARC target.</summary>
public static class DmarcTargetText
{
    public static string FollowsPolicy(DmarcPolicyLevel? policy) =>
        policy is { } level ? $"Follows p (currently {level})" : "Follows p";

    public static string Source(EffectiveDmarcTarget target) => target.Source switch
    {
        DmarcTargetSource.Domain => "set on this domain",
        DmarcTargetSource.System => "system-wide",
        _ when target.ContributingGroups.Count == 1 => $"from group {target.ContributingGroups[0]}",
        _ => $"strictest of {JoinNames(target.ContributingGroups)}"
    };

    public static string Policy(EffectiveDmarcTarget target) =>
        target.SubdomainPolicy == target.Policy
            ? $"p={Name(target.Policy)}"
            : $"p={Name(target.Policy)}; sp={Name(target.SubdomainPolicy)}";

    /// <summary>What a domain's record says, in the same form as <see cref="Policy"/>: sp only when it differs from p, and
    /// pct only when below 100.</summary>
    public static string Observed(DmarcPolicyTags observed)
    {
        var text = observed.SubdomainPolicy == observed.Policy
            ? $"p={Name(observed.Policy)}"
            : $"p={Name(observed.Policy)}; sp={Name(observed.SubdomainPolicy)}";
        return observed.Percent < 100 ? $"{text}; pct={observed.Percent}" : text;
    }

    /// <summary>A stored target as an audit field value: p=reject, p=reject; sp=quarantine, or "not set".</summary>
    public static string Stored(DmarcTarget? target) => target is null
        ? "not set"
        : target.SubdomainPolicy is { } subdomainPolicy
            ? $"p={Name(target.Policy)}; sp={Name(subdomainPolicy)}"
            : $"p={Name(target.Policy)}";

    private static string Name(DmarcPolicyLevel level) => level.ToString().ToLowerInvariant();

    private static string JoinNames(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Join("", names) : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
}
