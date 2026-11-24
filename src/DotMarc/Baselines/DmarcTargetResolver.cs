namespace DotMarc.Baselines;

/// <summary>Works out which DMARC target applies to a domain: its own override, else the strictest of its groups' targets
/// (p and sp separately), else the system target, else none. A group target overrides the system target even when weaker.</summary>
public static class DmarcTargetResolver
{
    public static EffectiveDmarcTarget? Resolve(
        DmarcTarget? domainTarget,
        IEnumerable<(string GroupName, DmarcTarget Target)> groupTargets,
        DmarcTarget? systemTarget)
    {
        if (domainTarget is not null)
        {
            return FromSingle(domainTarget, DmarcTargetSource.Domain);
        }

        var groups = groupTargets.OrderBy(group => group.GroupName, StringComparer.OrdinalIgnoreCase).ToList();
        if (groups.Count > 0)
        {
            // OrderBy is stable and the list is name-ordered, so on a tie the first by name wins.
            var policyWinner = groups.MaxBy(group => group.Target.Policy);
            var subdomainWinner = groups.MaxBy(group => group.Target.EffectiveSubdomainPolicy);

            return new EffectiveDmarcTarget(
                policyWinner.Target.Policy,
                subdomainWinner.Target.EffectiveSubdomainPolicy,
                DmarcTargetSource.Group,
                groups.Select(group => group.GroupName).ToList(),
                policyWinner.GroupName,
                subdomainWinner.GroupName);
        }

        return systemTarget is null ? null : FromSingle(systemTarget, DmarcTargetSource.System);
    }

    private static EffectiveDmarcTarget FromSingle(DmarcTarget target, DmarcTargetSource source) =>
        new(target.Policy, target.EffectiveSubdomainPolicy, source, [], null, null);
}
