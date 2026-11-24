using DotMarc.Baselines;
using DotMarc.Data;
using Xunit;

namespace DotMarc.Tests.Baselines;

/// <summary>Whether a domain's last stored DMARC observation meets its target, and how the target is described.</summary>
public sealed class DmarcTargetCheckTests
{
    private static readonly DateTimeOffset CheckedAt = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static EffectiveDmarcTarget Effective(DmarcPolicyLevel policy, DmarcPolicyLevel? subdomainPolicy = null) =>
        new(policy, subdomainPolicy ?? policy, DmarcTargetSource.System, [], null, null);

    private static Domain Checked(
        DmarcCheckStatus status,
        DmarcPolicyLevel? policy,
        DmarcPolicyLevel? subdomainPolicy,
        int? percent) => new()
    {
        Name = "contoso.example",
        DmarcCheckStatus = status,
        DmarcCheckedUtc = CheckedAt,
        DmarcPolicy = policy,
        DmarcSubdomainPolicy = subdomainPolicy,
        DmarcPercent = percent
    };

    [Fact]
    public void NoTarget_GivesNoTarget() =>
        Assert.Equal(DmarcTargetOutcome.NoTarget, DmarcTargetCheck.Outcome(Checked(DmarcCheckStatus.Ok, DmarcPolicyLevel.Reject, DmarcPolicyLevel.Reject, 100), null));

    [Fact]
    public void ANotCheckedDomain_NeedsChecking() =>
        Assert.Equal(
            DmarcTargetOutcome.NeedsChecking,
            DmarcTargetCheck.Outcome(new Domain { Name = "contoso.example", DmarcCheckStatus = DmarcCheckStatus.NotChecked }, Effective(DmarcPolicyLevel.Reject)));

    [Fact]
    public void ADomainWithNoCheckTime_NeedsChecking()
    {
        var domain = Checked(DmarcCheckStatus.Ok, DmarcPolicyLevel.Reject, DmarcPolicyLevel.Reject, 100);
        domain.DmarcCheckedUtc = null;

        Assert.Equal(DmarcTargetOutcome.NeedsChecking, DmarcTargetCheck.Outcome(domain, Effective(DmarcPolicyLevel.Reject)));
    }

    [Fact]
    public void AMissingOwnRecord_IsBelow() =>
        Assert.Equal(
            DmarcTargetOutcome.Below,
            DmarcTargetCheck.Outcome(Checked(DmarcCheckStatus.MissingOwnRecord, null, null, null), Effective(DmarcPolicyLevel.Quarantine)));

    [Fact]
    public void AMisconfiguredDomainWithNoPolicy_IsBelow() =>
        Assert.Equal(
            DmarcTargetOutcome.Below,
            DmarcTargetCheck.Outcome(Checked(DmarcCheckStatus.Misconfigured, null, null, null), Effective(DmarcPolicyLevel.Quarantine)));

    [Fact]
    public void AStricterPolicy_Meets() =>
        Assert.Equal(
            DmarcTargetOutcome.Meets,
            DmarcTargetCheck.Outcome(Checked(DmarcCheckStatus.Ok, DmarcPolicyLevel.Reject, DmarcPolicyLevel.Reject, 100), Effective(DmarcPolicyLevel.Quarantine)));

    [Fact]
    public void AWeakerSubdomainPolicy_IsBelow() =>
        Assert.Equal(
            DmarcTargetOutcome.Below,
            DmarcTargetCheck.Outcome(
                Checked(DmarcCheckStatus.Ok, DmarcPolicyLevel.Quarantine, DmarcPolicyLevel.None, 100),
                Effective(DmarcPolicyLevel.Quarantine, DmarcPolicyLevel.Quarantine)));

    [Fact]
    public void APercentageBelowAHundred_IsBelow() =>
        Assert.Equal(
            DmarcTargetOutcome.Below,
            DmarcTargetCheck.Outcome(Checked(DmarcCheckStatus.Ok, DmarcPolicyLevel.Reject, DmarcPolicyLevel.Reject, 50), Effective(DmarcPolicyLevel.Reject)));

    [Fact]
    public void AnEarlierOkCheck_IsJudgedOnTheLastStoredObservation() =>
        Assert.Equal(
            DmarcTargetOutcome.Below,
            DmarcTargetCheck.Outcome(Checked(DmarcCheckStatus.Ok, DmarcPolicyLevel.None, DmarcPolicyLevel.None, 100), Effective(DmarcPolicyLevel.Reject)));

    [Fact]
    public void SourceText_NamesWhereTheTargetCameFrom()
    {
        static EffectiveDmarcTarget From(DmarcTargetSource source, params string[] groups) =>
            new(DmarcPolicyLevel.Reject, DmarcPolicyLevel.Reject, source, groups, groups.FirstOrDefault(), groups.FirstOrDefault());

        Assert.Equal("set on this domain", DmarcTargetText.Source(From(DmarcTargetSource.Domain)));
        Assert.Equal("system-wide", DmarcTargetText.Source(From(DmarcTargetSource.System)));
        Assert.Equal("from group Contoso", DmarcTargetText.Source(From(DmarcTargetSource.Group, "Contoso")));
        Assert.Equal("strictest of Contoso and Parked-Estate", DmarcTargetText.Source(From(DmarcTargetSource.Group, "Contoso", "Parked-Estate")));
        Assert.Equal("strictest of A, B and C", DmarcTargetText.Source(From(DmarcTargetSource.Group, "A", "B", "C")));
    }

    [Fact]
    public void PolicyText_OmitsTheSubdomainPolicyWhenItMatches()
    {
        Assert.Equal("p=reject", DmarcTargetText.Policy(Effective(DmarcPolicyLevel.Reject)));
        Assert.Equal("p=reject; sp=quarantine", DmarcTargetText.Policy(Effective(DmarcPolicyLevel.Reject, DmarcPolicyLevel.Quarantine)));
    }
}
