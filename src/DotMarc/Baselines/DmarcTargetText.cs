using DotMarc.Data;

namespace DotMarc.Baselines;

/// <summary>Plain-language descriptions of an effective DMARC target.</summary>
public static class DmarcTargetText
{
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

    private static string Name(DmarcPolicyLevel level) => level.ToString().ToLowerInvariant();

    private static string JoinNames(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Join("", names) : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
}
