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

    private static Domain RejectingDomain() => Checked(DmarcCheckStatus.Ok, DmarcPolicyLevel.Reject, DmarcPolicyLevel.Reject, 100);

    [Fact]
    public void ABreakingProblem_IsBelowEvenWhenPMeetsTheTarget_AndNamesTheProblem()
    {
        var domain = RejectingDomain();
        domain.DmarcProblems = ["RepeatedTag:p"];

        Assert.Equal(DmarcTargetOutcome.Below, DmarcTargetCheck.Outcome(domain, Effective(DmarcPolicyLevel.Reject)));
        Assert.Equal(
            ["The record is malformed: The p tag appears more than once. Receivers may ignore the whole record"],
            DmarcTargetCheck.Reasons(domain, Effective(DmarcPolicyLevel.Reject)));
    }

    [Theory]
    [InlineData("ObsoleteTag:pct")]
    [InlineData("SomethingNew:x")]
    [InlineData("RepeatedTag")]
    public void AnInformationalOrUnknownStoredProblem_DoesNotCountAgainstTheTarget(string stored)
    {
        var domain = RejectingDomain();
        domain.DmarcProblems = [stored];

        Assert.Equal(DmarcTargetOutcome.Meets, DmarcTargetCheck.Outcome(domain, Effective(DmarcPolicyLevel.Reject)));
        Assert.Empty(DmarcTargetCheck.Reasons(domain, Effective(DmarcPolicyLevel.Reject)));
    }

    [Theory]
    [InlineData(DmarcPolicyLevel.Quarantine, DmarcTargetOutcome.Below)]
    [InlineData(DmarcPolicyLevel.Reject, DmarcTargetOutcome.Below)]
    [InlineData(DmarcPolicyLevel.None, DmarcTargetOutcome.Meets)]
    public void TestingMode_IsBelowForAnEnforcingTarget_AndMeetsForANoneTarget(DmarcPolicyLevel targetPolicy, DmarcTargetOutcome expected)
    {
        var domain = RejectingDomain();
        domain.DmarcTesting = true;

        var target = Effective(targetPolicy);

        Assert.Equal(expected, DmarcTargetCheck.Outcome(domain, target));
        Assert.Equal(expected == DmarcTargetOutcome.Below ? ["Testing mode (t=y) is on"] : [], DmarcTargetCheck.Reasons(domain, target));
    }

    [Fact]
    public void AWeakNonExistentSubdomainPolicy_IsBelow()
    {
        var domain = RejectingDomain();
        domain.DmarcNonExistentSubdomainPolicy = DmarcPolicyLevel.None;

        Assert.Equal(DmarcTargetOutcome.Below, DmarcTargetCheck.Outcome(domain, Effective(DmarcPolicyLevel.Reject)));
        Assert.Equal(["np is none, target for subdomains is reject"], DmarcTargetCheck.Reasons(domain, Effective(DmarcPolicyLevel.Reject)));
    }

    [Fact]
    public void AnAbsentNonExistentSubdomainPolicy_InheritsTheSubdomainPolicy()
    {
        var domain = Checked(DmarcCheckStatus.Ok, DmarcPolicyLevel.Reject, DmarcPolicyLevel.Quarantine, 100);

        Assert.Equal(["sp is quarantine, target is reject"], DmarcTargetCheck.Reasons(domain, Effective(DmarcPolicyLevel.Reject)));
        Assert.Equal(DmarcTargetOutcome.Meets, DmarcTargetCheck.Outcome(domain, Effective(DmarcPolicyLevel.Reject, DmarcPolicyLevel.Quarantine)));
    }

    [Fact]
    public void AStrongNonExistentSubdomainPolicy_CannotMakeUpForAWeakSubdomainPolicy()
    {
        var domain = Checked(DmarcCheckStatus.Ok, DmarcPolicyLevel.Reject, DmarcPolicyLevel.None, 100);
        domain.DmarcNonExistentSubdomainPolicy = DmarcPolicyLevel.Reject;

        Assert.Equal(["sp is none, target is reject"], DmarcTargetCheck.Reasons(domain, Effective(DmarcPolicyLevel.Reject)));
    }

    [Fact]
    public void AFullPercentage_Meets_AndAnythingLessIsAReason()
    {
        Assert.Empty(DmarcTargetCheck.Reasons(RejectingDomain(), Effective(DmarcPolicyLevel.Reject)));

        var partial = Checked(DmarcCheckStatus.Ok, DmarcPolicyLevel.Reject, DmarcPolicyLevel.Reject, 50);
        Assert.Equal(["pct is 50"], DmarcTargetCheck.Reasons(partial, Effective(DmarcPolicyLevel.Reject)));
    }

    [Fact]
    public void Reasons_AreOrderedProblemsThenPolicyThenTestingThenPercent()
    {
        var domain = Checked(DmarcCheckStatus.Misconfigured, DmarcPolicyLevel.None, DmarcPolicyLevel.Quarantine, 50);
        domain.DmarcNonExistentSubdomainPolicy = DmarcPolicyLevel.None;
        domain.DmarcTesting = true;
        domain.DmarcProblems = ["TestingMode", "MissingPolicy"];

        Assert.Equal(
            [
                "The record is malformed: The record has no p tag. Receivers read it as p=none, or apply no DMARC processing without a valid rua",
                "p is none, target is reject",
                "sp is quarantine, target is reject",
                "np is none, target for subdomains is reject",
                "Testing mode (t=y) is on",
                "pct is 50"
            ],
            DmarcTargetCheck.Reasons(domain, Effective(DmarcPolicyLevel.Reject)));
    }

    [Fact]
    public void Reasons_AreEmptyWithoutATarget_OrWhenTheDomainNeedsChecking()
    {
        Assert.Empty(DmarcTargetCheck.Reasons(Checked(DmarcCheckStatus.Ok, DmarcPolicyLevel.None, DmarcPolicyLevel.None, 100), null));
        Assert.Empty(DmarcTargetCheck.Reasons(new Domain { Name = "contoso.example", DmarcCheckStatus = DmarcCheckStatus.NotChecked }, Effective(DmarcPolicyLevel.Reject)));
    }

    [Fact]
    public void AMissingRecord_IsBelowBecauseThereIsNoRecord() =>
        Assert.Equal(
            ["There is no DMARC record."],
            DmarcTargetCheck.Reasons(Checked(DmarcCheckStatus.MissingOwnRecord, null, null, null), Effective(DmarcPolicyLevel.Quarantine)));

    [Fact]
    public void Outcome_IsBelowExactlyWhenThereAreReasons()
    {
        var target = Effective(DmarcPolicyLevel.Reject);
        var domains = new[]
        {
            RejectingDomain(),
            Checked(DmarcCheckStatus.Misconfigured, null, null, null),
            Checked(DmarcCheckStatus.MissingOwnRecord, null, null, null),
            Checked(DmarcCheckStatus.Ok, DmarcPolicyLevel.Quarantine, DmarcPolicyLevel.Quarantine, 100)
        };

        foreach (var domain in domains)
        {
            Assert.Equal(DmarcTargetCheck.Reasons(domain, target).Count > 0, DmarcTargetCheck.Outcome(domain, target) == DmarcTargetOutcome.Below);
        }
    }

    [Fact]
    public void StoredProblems_ReadBackWithTheAnalysersWording_AndUnknownOnesAreIgnored()
    {
        var problem = DotMarc.Dns.DmarcRecordProblem.Parse("RepeatedTag:p");

        Assert.Equal(DotMarc.Dns.DmarcRecordProblemKind.RepeatedTag, problem?.Kind);
        Assert.Equal(DotMarc.Dns.DmarcRecordAnalysis.Analyse(["v=DMARC1; p=none; p=reject"]).Problems.Single().Description, problem?.Description);
        Assert.Null(DotMarc.Dns.DmarcRecordProblem.Parse("NotAKind"));
        Assert.Null(DotMarc.Dns.DmarcRecordProblem.Parse("99"));
    }

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

    [Fact]
    public void ABarePolicyOfNone_GivesOnlyThePolicyReason()
    {
        var domain = Checked(DmarcCheckStatus.Ok, DmarcPolicyLevel.None, null, 100);

        Assert.Equal(["p is none, target is reject"], DmarcTargetCheck.Reasons(domain, Effective(DmarcPolicyLevel.Reject)));
    }

    [Fact]
    public void AnInheritedSubdomainPolicyBelowTheTarget_IsBelowWithAReasonWhenPMeetsIt()
    {
        var domain = Checked(DmarcCheckStatus.Ok, DmarcPolicyLevel.Reject, null, 100);
        var target = Effective(DmarcPolicyLevel.Quarantine, DmarcPolicyLevel.Reject);
        domain.DmarcPolicy = DmarcPolicyLevel.Quarantine;

        Assert.Equal(DmarcTargetOutcome.Below, DmarcTargetCheck.Outcome(domain, target));
        Assert.Equal(["sp follows p (quarantine), target for subdomains is reject"], DmarcTargetCheck.Reasons(domain, target));
    }

    [Fact]
    public void AMalformedReason_HasNoTrailingFullStop()
    {
        var domain = RejectingDomain();
        domain.DmarcProblems = ["RepeatedTag:p"];

        Assert.DoesNotContain(DmarcTargetCheck.Reasons(domain, Effective(DmarcPolicyLevel.Reject)), reason => reason.EndsWith('.'));
    }
}
