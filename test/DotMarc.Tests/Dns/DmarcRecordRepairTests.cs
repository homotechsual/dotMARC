using DotMarc.Baselines;
using DotMarc.Data;
using DotMarc.Dns;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class DmarcRecordRepairTests
{
    private static DmarcRecordRepairResult? Build(EffectiveDmarcTarget? target, params string[] txtValues) =>
        DmarcRecordRepair.Build(DmarcRecordAnalysis.Analyse(txtValues), target);

    private static EffectiveDmarcTarget Target(DmarcPolicyLevel policy, DmarcPolicyLevel? subdomainPolicy = null) =>
        new(policy, subdomainPolicy ?? policy, DmarcTargetSource.System, [], null, null);

    private static void AssertRepair(DmarcRecordRepairResult? result, string expectedRecord, params string[] expectedChanges)
    {
        Assert.NotNull(result);
        Assert.Equal(expectedRecord, result.Record);
        Assert.Equal(expectedChanges, result.Changes);
    }

    [Fact]
    public void NoDmarcRecord_ReturnsNull()
    {
        Assert.Null(Build(null, "google-site-verification=abc"));
        Assert.Null(Build(Target(DmarcPolicyLevel.Reject), "google-site-verification=abc"));
    }

    [Fact]
    public void CleanRecordWithNoTarget_ReturnsNull()
    {
        Assert.Null(Build(null, "v=DMARC1; p=reject; rua=mailto:a@x"));
    }

    [Fact]
    public void InformationalProblemsAloneWithNoTarget_ReturnNull()
    {
        Assert.Null(Build(null, "v=DMARC1; p=quarantine; pct=50; t=y"));
    }

    [Fact]
    public void RepeatedPolicy_KeepsTheStrictestInPlace()
    {
        AssertRepair(Build(null, "v=DMARC1; p=none; p=reject; rua=mailto:a@x"),
            "v=DMARC1; p=reject; rua=mailto:a@x",
            "Removed the repeated p=none.");
    }

    [Fact]
    public void RepeatedPolicyWithEqualValues_KeepsOne()
    {
        AssertRepair(Build(null, "v=DMARC1; p=none; p=none"),
            "v=DMARC1; p=none",
            "Removed the repeated p=none.");
    }

    [Fact]
    public void RepeatedOtherTag_KeepsTheFirstValue()
    {
        AssertRepair(Build(null, "v=DMARC1; p=reject; rua=mailto:a@x; rua=mailto:b@x"),
            "v=DMARC1; p=reject; rua=mailto:a@x",
            "Removed the repeated rua=mailto:b@x.");
    }

    [Fact]
    public void TwoRecords_AreCombinedByTheSameRules()
    {
        AssertRepair(Build(null, "v=DMARC1; p=none; rua=mailto:a@x", "v=DMARC1; p=reject"),
            "v=DMARC1; p=reject; rua=mailto:a@x",
            "Combined 2 DMARC records into one.",
            "Removed the repeated p=none.");
    }

    [Fact]
    public void VersionFirst_IsFixed()
    {
        AssertRepair(Build(null, "p=reject; v=DMARC1"),
            "v=DMARC1; p=reject",
            "Moved v=DMARC1 to the front.");
        AssertRepair(Build(null, "v=dmarc1; p=reject"),
            "v=DMARC1; p=reject",
            "Set the version to v=DMARC1.");
    }

    [Fact]
    public void MissingPolicy_IsWrittenAsNone()
    {
        AssertRepair(Build(null, "v=DMARC1; rua=mailto:a@x"),
            "v=DMARC1; p=none; rua=mailto:a@x",
            "Added p=none because the record had no p.");
    }

    [Fact]
    public void InvalidPolicy_BecomesNone()
    {
        AssertRepair(Build(null, "v=DMARC1; p=rejct"),
            "v=DMARC1; p=none",
            "Replaced the invalid p=rejct with p=none.");
    }

    [Fact]
    public void InvalidSubdomainAndNonExistentPolicies_AreDropped()
    {
        AssertRepair(Build(null, "v=DMARC1; p=reject; sp=bogus; np=bogus"),
            "v=DMARC1; p=reject",
            "Removed the invalid sp=bogus.",
            "Removed the invalid np=bogus.");
    }

    [Fact]
    public void ObsoleteTagsWithNoTarget_KeepPctAndDropRfAndRi()
    {
        AssertRepair(Build(null, "v=DMARC1; p=quarantine; pct=50; rf=afrf; ri=86400"),
            "v=DMARC1; p=quarantine; pct=50",
            "Removed the obsolete rf=afrf.",
            "Removed the obsolete ri=86400.");
    }

    [Fact]
    public void ObsoleteTagsWithTarget_DropPctAndRaiseThePolicy()
    {
        AssertRepair(Build(Target(DmarcPolicyLevel.Reject), "v=DMARC1; p=quarantine; pct=50; rf=afrf"),
            "v=DMARC1; p=reject",
            "Removed the obsolete pct=50.",
            "Removed the obsolete rf=afrf.",
            "Raised p from quarantine to reject.");
    }

    [Fact]
    public void TestingModeWithTarget_IsRemoved()
    {
        AssertRepair(Build(Target(DmarcPolicyLevel.Reject), "v=DMARC1; p=reject; t=y"),
            "v=DMARC1; p=reject",
            "Removed t=y (testing mode).");
    }

    [Fact]
    public void RepeatedTestingTag_KeepsNoOverYes()
    {
        AssertRepair(Build(null, "v=DMARC1; p=reject; t=y; t=n"),
            "v=DMARC1; p=reject; t=n",
            "Removed the repeated t=y.");
        AssertRepair(Build(null, "v=DMARC1; p=reject; t=n; t=y"),
            "v=DMARC1; p=reject; t=n",
            "Removed the repeated t=y.");
    }

    [Fact]
    public void RepeatedTestingTagWithSameValue_KeepsOneWithNoTarget()
    {
        AssertRepair(Build(null, "v=DMARC1; p=reject; t=y; t=y"),
            "v=DMARC1; p=reject; t=y",
            "Removed the repeated t=y.");
    }

    [Fact]
    public void TestingModeNoWithTarget_NeedsNoChange()
    {
        Assert.Null(Build(Target(DmarcPolicyLevel.Reject), "v=DMARC1; p=reject; t=n"));
    }

    [Fact]
    public void WeakerNonExistentPolicy_IsRaisedToTheTargetSubdomainPolicy()
    {
        AssertRepair(Build(Target(DmarcPolicyLevel.Reject), "v=DMARC1; p=reject; np=none"),
            "v=DMARC1; p=reject; np=reject",
            "Raised np from none to reject.");
    }

    [Fact]
    public void WeakerSubdomainPolicy_IsRaisedToTheTargetSubdomainPolicy()
    {
        AssertRepair(Build(Target(DmarcPolicyLevel.Reject, DmarcPolicyLevel.Quarantine), "v=DMARC1; p=reject; sp=none"),
            "v=DMARC1; p=reject; sp=quarantine",
            "Raised sp from none to quarantine.");
    }

    [Fact]
    public void RaisingPolicyWithNoSubdomainPolicy_WritesTheTargetSubdomainPolicy()
    {
        AssertRepair(Build(Target(DmarcPolicyLevel.Reject, DmarcPolicyLevel.Quarantine), "v=DMARC1; p=none; rua=mailto:a@x"),
            "v=DMARC1; p=reject; sp=quarantine; rua=mailto:a@x",
            "Raised p from none to reject.",
            "Set sp to quarantine so subdomains keep their policy.");
        AssertRepair(Build(Target(DmarcPolicyLevel.Reject, DmarcPolicyLevel.None), "v=DMARC1; p=none"),
            "v=DMARC1; p=reject; sp=none",
            "Raised p from none to reject.",
            "Set sp to none so subdomains keep their policy.");
    }

    [Fact]
    public void RaisingPolicyKeepsTheStricterOriginalSubdomainBehaviour()
    {
        AssertRepair(Build(Target(DmarcPolicyLevel.Reject, DmarcPolicyLevel.Quarantine), "v=DMARC1; p=quarantine"),
            "v=DMARC1; p=reject; sp=quarantine",
            "Raised p from quarantine to reject.",
            "Set sp to quarantine so subdomains keep their policy.");
    }

    [Fact]
    public void PolicyAlreadyAtTargetWithTargetSubdomainPolicyNone_NeedsNoChange()
    {
        Assert.Null(Build(Target(DmarcPolicyLevel.Reject, DmarcPolicyLevel.None), "v=DMARC1; p=reject"));
    }

    [Fact]
    public void TwoNonOverlappingRecords_AreCombined()
    {
        AssertRepair(Build(null, "v=DMARC1; p=reject", "v=DMARC1; rua=mailto:a@x"),
            "v=DMARC1; p=reject; rua=mailto:a@x",
            "Combined 2 DMARC records into one.");
    }

    [Fact]
    public void SubdomainPolicyIsAddedWhenPolicyStaysBelowTheTargetSubdomainPolicy()
    {
        AssertRepair(Build(Target(DmarcPolicyLevel.None, DmarcPolicyLevel.Reject), "v=DMARC1; p=none"),
            "v=DMARC1; p=none; sp=reject",
            "Added sp=reject.");
    }

    [Fact]
    public void RecordAlreadyStricterThanTarget_ReturnsNull()
    {
        Assert.Null(Build(Target(DmarcPolicyLevel.Quarantine), "v=DMARC1; p=reject"));
    }

    [Fact]
    public void ChangesAreOrderedVersionRepeatsInvalidObsoleteRaisesTesting()
    {
        var result = Build(Target(DmarcPolicyLevel.Reject), "p=none; p=none; sp=bogus; pct=50; t=y; v=DMARC1");

        AssertRepair(result,
            "v=DMARC1; p=reject",
            "Moved v=DMARC1 to the front.",
            "Removed the repeated p=none.",
            "Removed the invalid sp=bogus.",
            "Removed the obsolete pct=50.",
            "Raised p from none to reject.",
            "Removed t=y (testing mode).");
    }
}
