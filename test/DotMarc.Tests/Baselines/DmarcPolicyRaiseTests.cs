using DotMarc.Baselines;
using DotMarc.Data;
using Xunit;

namespace DotMarc.Tests.Baselines;

/// <summary>How the DNS push raises a DMARC record to its target without ever lowering it.</summary>
public sealed class DmarcPolicyRaiseTests
{
    private static EffectiveDmarcTarget Target(DmarcPolicyLevel policy, DmarcPolicyLevel? subdomainPolicy = null) =>
        new(policy, subdomainPolicy ?? policy, DmarcTargetSource.System, [], null, null);

    [Fact]
    public void RaisesPolicyToTheTarget() =>
        Assert.Equal("v=DMARC1; p=reject; rua=mailto:a@x", DmarcPolicyRaise.Apply("v=DMARC1; p=none; rua=mailto:a@x", Target(DmarcPolicyLevel.Reject)));

    [Fact]
    public void RaisesSubdomainPolicyAndRemovesAPartialPercentage() =>
        Assert.Equal(
            "v=DMARC1; p=quarantine; sp=quarantine; fo=1",
            DmarcPolicyRaise.Apply("v=DMARC1; p=quarantine; sp=none; pct=50; fo=1", Target(DmarcPolicyLevel.Quarantine, DmarcPolicyLevel.Quarantine)));

    [Fact]
    public void NeverLowersAStricterPolicy() =>
        Assert.Equal("v=DMARC1; p=reject", DmarcPolicyRaise.Apply("v=DMARC1; p=reject", Target(DmarcPolicyLevel.Quarantine)));

    [Fact]
    public void AddsASubdomainPolicyWhenTheTargetIsStricterThanThePolicy() =>
        Assert.Equal("v=DMARC1; p=quarantine; sp=reject", DmarcPolicyRaise.Apply("v=DMARC1; p=none", Target(DmarcPolicyLevel.Quarantine, DmarcPolicyLevel.Reject)));

    [Fact]
    public void DoesNotAddASubdomainPolicyThePolicyAlreadyCovers() =>
        Assert.Equal("v=DMARC1; p=reject", DmarcPolicyRaise.Apply("v=DMARC1; p=none", Target(DmarcPolicyLevel.Reject, DmarcPolicyLevel.None)));

    [Fact]
    public void RecognisesUpperCaseTagNamesAndKeepsTheCasingOfOthers() =>
        Assert.Equal("v=DMARC1; P=reject; RUA=mailto:a@x", DmarcPolicyRaise.Apply("v=DMARC1; P=none; RUA=mailto:a@x", Target(DmarcPolicyLevel.Reject)));

    [Fact]
    public void LeavesARecordThatAlreadyMeetsTheTargetUntouched()
    {
        const string record = "v=DMARC1;p=reject;  sp=reject ;rua=mailto:a@x;";

        Assert.Equal(record, DmarcPolicyRaise.Apply(record, Target(DmarcPolicyLevel.Quarantine)));
    }

    [Fact]
    public void RaisesInPlaceKeepingTheRecordsSpacing() =>
        Assert.Equal("v=DMARC1;p=reject;rua=mailto:a@x", DmarcPolicyRaise.Apply("v=DMARC1;p=none;rua=mailto:a@x", Target(DmarcPolicyLevel.Reject)));

    [Fact]
    public void RemovesAPartialPercentageEvenWhenThePolicyMeetsTheTarget() =>
        Assert.Equal("v=DMARC1; p=reject; rua=mailto:a@x", DmarcPolicyRaise.Apply("v=DMARC1; pct=25; p=reject; rua=mailto:a@x", Target(DmarcPolicyLevel.Reject)));

    [Fact]
    public void AddsAMissingPolicyAfterTheVersion() =>
        Assert.Equal("v=DMARC1; p=reject; rua=mailto:a@x", DmarcPolicyRaise.Apply("v=DMARC1; rua=mailto:a@x", Target(DmarcPolicyLevel.Reject)));

    [Fact]
    public void NewRecordPolicy_IsNoneWithoutATarget() =>
        Assert.Equal("p=none", DmarcPolicyRaise.NewRecordPolicy(null));

    [Fact]
    public void NewRecordPolicy_IsTheTargetsText() =>
        Assert.Equal("p=reject; sp=quarantine", DmarcPolicyRaise.NewRecordPolicy(Target(DmarcPolicyLevel.Reject, DmarcPolicyLevel.Quarantine)));
}
