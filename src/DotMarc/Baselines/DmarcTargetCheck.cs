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
    /// <summary>Judges the domain's last stored DMARC observation against its target, by the rules of RFC 9989. A domain
    /// meets it when its record has no breaking problem, p, sp and np are at or above the target, testing mode is not
    /// weakening an enforcing target, and pct is 100. A domain never checked needs checking first.</summary>
    public static DmarcTargetOutcome Outcome(Domain domain, EffectiveDmarcTarget? target)
    {
        if (target is null)
        {
            return DmarcTargetOutcome.NoTarget;
        }

        if (NeedsChecking(domain))
        {
            return DmarcTargetOutcome.NeedsChecking;
        }

        return BelowReasons(domain, target).Count == 0 ? DmarcTargetOutcome.Meets : DmarcTargetOutcome.Below;
    }

    /// <summary>The plain-words reasons a domain is below its target, empty when it meets the target, has none, or needs
    /// checking. Breaking record problems come first, then p, sp and np, then testing mode, then pct.</summary>
    public static IReadOnlyList<string> Reasons(Domain domain, EffectiveDmarcTarget? target) =>
        target is null || NeedsChecking(domain) ? [] : BelowReasons(domain, target);

    private static bool NeedsChecking(Domain domain) =>
        domain.DmarcCheckStatus == DmarcCheckStatus.NotChecked || domain.DmarcCheckedUtc is null;

    private static List<string> BelowReasons(Domain domain, EffectiveDmarcTarget target)
    {
        if (domain.DmarcCheckStatus == DmarcCheckStatus.MissingOwnRecord)
        {
            return ["There is no DMARC record."];
        }

        var reasons = domain.DmarcProblems
            .Select(DmarcRecordProblem.Parse)
            .OfType<DmarcRecordProblem>()
            .Where(problem => problem.Kind.IsBreaking())
            .Select(problem => $"The record is malformed: {problem.Description}")
            .ToList();

        if (domain.DmarcPolicy is not { } policy)
        {
            if (reasons.Count == 0)
            {
                reasons.Add("The DMARC record has no readable policy.");
            }

            return reasons;
        }

        var subdomainPolicy = domain.DmarcSubdomainPolicy ?? policy;
        var nonExistentPolicy = domain.DmarcNonExistentSubdomainPolicy ?? subdomainPolicy;
        if (policy < target.Policy)
        {
            reasons.Add($"p is {Name(policy)}, target is {Name(target.Policy)}");
        }

        if (subdomainPolicy < target.SubdomainPolicy)
        {
            reasons.Add($"sp is {Name(subdomainPolicy)}, target is {Name(target.SubdomainPolicy)}");
        }

        if (nonExistentPolicy < target.SubdomainPolicy)
        {
            reasons.Add($"np is {Name(nonExistentPolicy)}, target for subdomains is {Name(target.SubdomainPolicy)}");
        }

        // t has no effect on none, so it only counts against a target that asks for enforcement.
        if (domain.DmarcTesting && (target.Policy > DmarcPolicyLevel.None || target.SubdomainPolicy > DmarcPolicyLevel.None))
        {
            reasons.Add("Testing mode (t=y) is on");
        }

        if ((domain.DmarcPercent ?? 100) < 100)
        {
            reasons.Add($"pct is {domain.DmarcPercent}");
        }

        return reasons;
    }

    private static string Name(DmarcPolicyLevel level) => level.ToString().ToLowerInvariant();
}
