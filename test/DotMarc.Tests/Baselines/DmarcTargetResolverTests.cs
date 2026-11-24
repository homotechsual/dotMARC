using DotMarc.Baselines;
using DotMarc.Data;
using Xunit;

namespace DotMarc.Tests.Baselines;

/// <summary>Which DMARC target applies to a domain: its own override, else its groups' strictest, else the system target.</summary>
public sealed class DmarcTargetResolverTests
{
    private static DmarcTarget Target(DmarcPolicyLevel policy, DmarcPolicyLevel? subdomainPolicy = null) => new(policy, subdomainPolicy);

    private static (string GroupName, DmarcTarget Target) Group(string name, DmarcTarget target) => (name, target);

    [Fact]
    public void NothingSetAtAnyLevel_GivesNoTarget() =>
        Assert.Null(DmarcTargetResolver.Resolve(null, [], null));

    [Fact]
    public void SystemOnly_GivesTheSystemTarget()
    {
        var effective = DmarcTargetResolver.Resolve(null, [], Target(DmarcPolicyLevel.Reject));

        Assert.NotNull(effective);
        Assert.Equal(DmarcTargetSource.System, effective.Source);
        Assert.Equal(DmarcPolicyLevel.Reject, effective.Policy);
        Assert.Empty(effective.ContributingGroups);
        Assert.Null(effective.PolicyGroup);
    }

    [Fact]
    public void AGroupTarget_BeatsTheSystemTarget_EvenWhenWeaker()
    {
        var effective = DmarcTargetResolver.Resolve(
            null,
            [Group("Parked", Target(DmarcPolicyLevel.None))],
            Target(DmarcPolicyLevel.Reject));

        Assert.NotNull(effective);
        Assert.Equal(DmarcTargetSource.Group, effective.Source);
        Assert.Equal(DmarcPolicyLevel.None, effective.Policy);
        Assert.Equal(DmarcPolicyLevel.None, effective.SubdomainPolicy);
    }

    [Fact]
    public void ADomainOverride_BeatsGroupsAndSystem()
    {
        var effective = DmarcTargetResolver.Resolve(
            Target(DmarcPolicyLevel.Quarantine),
            [Group("Contoso", Target(DmarcPolicyLevel.Reject))],
            Target(DmarcPolicyLevel.Reject));

        Assert.NotNull(effective);
        Assert.Equal(DmarcTargetSource.Domain, effective.Source);
        Assert.Equal(DmarcPolicyLevel.Quarantine, effective.Policy);
        Assert.Null(effective.PolicyGroup);
    }

    [Fact]
    public void TwoGroups_TakeTheStrictest_AndNameTheWinner()
    {
        var effective = DmarcTargetResolver.Resolve(
            null,
            [Group("Parked-Estate", Target(DmarcPolicyLevel.Quarantine)), Group("Contoso", Target(DmarcPolicyLevel.Reject))],
            null);

        Assert.NotNull(effective);
        Assert.Equal(DmarcTargetSource.Group, effective.Source);
        Assert.Equal(DmarcPolicyLevel.Reject, effective.Policy);
        Assert.Equal("Contoso", effective.PolicyGroup);
        Assert.Equal(["Contoso", "Parked-Estate"], effective.ContributingGroups);
    }

    [Fact]
    public void EachTagIsResolvedSeparately()
    {
        var effective = DmarcTargetResolver.Resolve(
            null,
            [Group("A", Target(DmarcPolicyLevel.Reject)), Group("B", Target(DmarcPolicyLevel.None, DmarcPolicyLevel.Reject))],
            null);

        Assert.NotNull(effective);
        Assert.Equal(DmarcPolicyLevel.Reject, effective.Policy);
        Assert.Equal("A", effective.PolicyGroup);
        Assert.Equal(DmarcPolicyLevel.Reject, effective.SubdomainPolicy);
    }

    [Fact]
    public void ASubdomainPolicyAlone_OnAGroupWithPolicyNone_ResolvesFromThatGroup()
    {
        var effective = DmarcTargetResolver.Resolve(
            null,
            [Group("Strict-Subs", Target(DmarcPolicyLevel.None, DmarcPolicyLevel.Quarantine))],
            null);

        Assert.NotNull(effective);
        Assert.Equal(DmarcPolicyLevel.None, effective.Policy);
        Assert.Equal(DmarcPolicyLevel.Quarantine, effective.SubdomainPolicy);
        Assert.Equal("Strict-Subs", effective.SubdomainGroup);
    }

    [Fact]
    public void AnUnsetSubdomainPolicy_FollowsThatLevelsPolicy()
    {
        var effective = DmarcTargetResolver.Resolve(null, [], Target(DmarcPolicyLevel.Quarantine));

        Assert.NotNull(effective);
        Assert.Equal(DmarcPolicyLevel.Quarantine, effective.SubdomainPolicy);
    }

    [Fact]
    public void ADomainOverrideWithPolicyOnly_ReplacesTheWholeTarget_NotTakingAGroupsStricterSubdomainPolicy()
    {
        var effective = DmarcTargetResolver.Resolve(
            Target(DmarcPolicyLevel.Quarantine),
            [Group("Contoso", Target(DmarcPolicyLevel.Reject, DmarcPolicyLevel.Reject))],
            null);

        Assert.NotNull(effective);
        Assert.Equal(DmarcPolicyLevel.Quarantine, effective.SubdomainPolicy);
    }
}
