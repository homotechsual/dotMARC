namespace DotMarc.Data;

/// <summary>A user-defined container a domain can belong to - typically a client/owner in the
/// MSP use case this app is designed for, though a domain can belong to more than one Group at
/// once. Carries no access-control meaning on its own; that's the subject of a later design
/// cycle, not this one.</summary>
public sealed class Group
{
    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>The DMARC policy this group should reach. Null when none is set.</summary>
    public DmarcPolicyLevel? TargetDmarcPolicy { get; set; }

    /// <summary>The target subdomain policy. Null follows <see cref="TargetDmarcPolicy"/>.</summary>
    public DmarcPolicyLevel? TargetDmarcSubdomainPolicy { get; set; }
    public bool OverrideAdditionalRuaAddress { get; set; }
    public string? AdditionalRuaAddress { get; set; }

    /// <summary>The target this row holds, or null when none is set. An unset subdomain policy follows the policy.</summary>
    public DotMarc.Baselines.DmarcTarget? DmarcTarget =>
        TargetDmarcPolicy is { } policy ? new(policy, TargetDmarcSubdomainPolicy) : null;
    public List<Domain> Domains { get; set; } = [];
    public List<DotMarc.Psa.PsaCompanyLink> PsaCompanyLinks { get; set; } = [];
}
