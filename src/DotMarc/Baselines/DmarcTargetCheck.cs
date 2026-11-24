using DotMarc.Data;
using DotMarc.Dns;

namespace DotMarc.Baselines;

/// <summary>Whether a domain meets its DMARC target.</summary>
public enum DmarcTargetOutcome
{
    NoTarget,
    Meets,
    Below,
    NeedsChecking
}

public static class DmarcTargetCheck
{
    /// <summary>Judges the domain's last stored DMARC observation against its target. Meeting it needs p and sp at or above
    /// the target and an effective pct of 100. A domain never checked needs checking first.</summary>
    public static DmarcTargetOutcome Outcome(Domain domain, EffectiveDmarcTarget? target)
    {
        if (target is null)
        {
            return DmarcTargetOutcome.NoTarget;
        }

        if (domain.DmarcCheckStatus == DmarcCheckStatus.NotChecked || domain.DmarcCheckedUtc is null)
        {
            return DmarcTargetOutcome.NeedsChecking;
        }

        var observed = DmarcPolicyTags.Of(domain);
        var meets = observed is not null
            && observed.Policy >= target.Policy
            && observed.SubdomainPolicy >= target.SubdomainPolicy
            && observed.Percent == 100;

        return meets ? DmarcTargetOutcome.Meets : DmarcTargetOutcome.Below;
    }
}
