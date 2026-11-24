using DotMarc.Data;

namespace DotMarc.Baselines;

/// <summary>A target DMARC policy as stored at any level (system, group or domain). An unset subdomain policy follows the policy.</summary>
public sealed record DmarcTarget(DmarcPolicyLevel Policy, DmarcPolicyLevel? SubdomainPolicy)
{
    public DmarcPolicyLevel EffectiveSubdomainPolicy => SubdomainPolicy ?? Policy;
}

/// <summary>Where a domain's effective target came from.</summary>
public enum DmarcTargetSource
{
    Domain,
    Group,
    System
}

/// <summary>The target that applies to one domain. The group fields are set only when <see cref="Source"/> is
/// <see cref="DmarcTargetSource.Group"/>: <paramref name="ContributingGroups"/> lists every group of the domain with a
/// target (by name), and <paramref name="PolicyGroup"/> and <paramref name="SubdomainGroup"/> name the group that set each tag.</summary>
public sealed record EffectiveDmarcTarget(
    DmarcPolicyLevel Policy,
    DmarcPolicyLevel SubdomainPolicy,
    DmarcTargetSource Source,
    IReadOnlyList<string> ContributingGroups,
    string? PolicyGroup,
    string? SubdomainGroup);
