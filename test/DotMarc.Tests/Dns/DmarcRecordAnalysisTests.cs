using DotMarc.Data;
using DotMarc.Dns;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class DmarcRecordAnalysisTests
{
    private static DmarcRecordAnalysis Analyse(params string[] txtValues) => DmarcRecordAnalysis.Analyse(txtValues);

    [Fact]
    public void CleanRecord_IsValidWithNoProblems()
    {
        var analysis = Analyse("v=DMARC1; p=reject; rua=mailto:a@example.com");

        Assert.True(analysis.IsValid);
        Assert.Empty(analysis.Problems);
        Assert.Equal(DmarcPolicyLevel.Reject, analysis.Policy);
    }

    [Fact]
    public void LowerCaseVersionValue_IsBreaking()
    {
        var analysis = Analyse("v=dmarc1; p=reject");

        Assert.False(analysis.IsValid);
        Assert.Contains(analysis.Problems, problem => problem.Kind == DmarcRecordProblemKind.VersionMissingOrNotFirst);
        Assert.Equal(DmarcPolicyLevel.Reject, analysis.Policy);
    }

    [Fact]
    public void VersionNotFirst_IsBreaking()
    {
        var analysis = Analyse("p=reject; v=DMARC1");

        Assert.False(analysis.IsValid);
        Assert.Contains(analysis.Problems, problem => problem.Kind == DmarcRecordProblemKind.VersionMissingOrNotFirst);
    }

    [Fact]
    public void TwoDmarcRecords_GiveMultipleRecords()
    {
        var analysis = Analyse("v=DMARC1; p=none", "v=DMARC1; p=reject");

        Assert.Contains(analysis.Problems, problem => problem.Kind == DmarcRecordProblemKind.MultipleRecords);
        Assert.False(analysis.IsValid);
    }

    [Fact]
    public void NonDmarcValue_IsIgnored()
    {
        var analysis = Analyse("v=DMARC1; p=reject", "google-site-verification=abc");

        Assert.Single(analysis.Records);
        Assert.True(analysis.IsValid);
    }

    [Fact]
    public void NoDmarcValueAtAll_HasNoRecordsAndNoProblems()
    {
        var analysis = Analyse("google-site-verification=abc");

        Assert.Empty(analysis.Records);
        Assert.Empty(analysis.Problems);
        Assert.Null(analysis.Policy);
    }

    [Fact]
    public void CandidateIsUsedOnlyWhenNoProperRecordExists()
    {
        var withProper = Analyse("v=dmarc1; p=none", "v=DMARC1; p=reject");
        var withoutProper = Analyse("v=dmarc1; p=none");

        Assert.Equal(["v=DMARC1; p=reject"], withProper.Records);
        Assert.True(withProper.IsValid);
        Assert.Equal(["v=dmarc1; p=none"], withoutProper.Records);
        Assert.False(withoutProper.IsValid);
    }

    [Fact]
    public void IgnoredCandidates_ListsLookAlikesOnlyWhenAProperRecordExists()
    {
        Assert.Equal(["v=dmarc1; p=none"], Analyse("v=dmarc1; p=none", "v=DMARC1; p=reject").IgnoredCandidates);
        Assert.Empty(Analyse("v=dmarc1; p=none").IgnoredCandidates);
        Assert.Empty(Analyse("v=DMARC1; p=reject").IgnoredCandidates);
    }

    [Fact]
    public void RepeatedTag_IsReportedOncePerTagAndPolicyIsTheStrictest()
    {
        var analysis = Analyse("v=DMARC1; p=none; p=reject; p=quarantine");

        var repeated = Assert.Single(analysis.Problems, problem => problem.Kind == DmarcRecordProblemKind.RepeatedTag);
        Assert.Equal("p", repeated.Tag);
        Assert.Equal(DmarcPolicyLevel.Reject, analysis.Policy);
        Assert.False(analysis.IsValid);
    }

    [Fact]
    public void MissingPolicy_IsBreaking()
    {
        var analysis = Analyse("v=DMARC1; rua=mailto:a@example.com");

        Assert.Contains(analysis.Problems, problem => problem.Kind == DmarcRecordProblemKind.MissingPolicy);
        Assert.Null(analysis.Policy);
        Assert.False(analysis.IsValid);
    }

    [Fact]
    public void InvalidPolicy_IsBreaking()
    {
        var analysis = Analyse("v=DMARC1; p=rejct");

        Assert.Contains(analysis.Problems, problem => problem.Kind == DmarcRecordProblemKind.InvalidPolicy);
        Assert.Null(analysis.Policy);
    }

    [Fact]
    public void InvalidSubdomainAndNonExistentPolicies_AreBreaking()
    {
        var analysis = Analyse("v=DMARC1; p=reject; sp=bogus; np=bogus");

        Assert.Contains(analysis.Problems, problem => problem.Kind == DmarcRecordProblemKind.InvalidSubdomainPolicy);
        Assert.Contains(analysis.Problems, problem => problem.Kind == DmarcRecordProblemKind.InvalidNonExistentPolicy);
        Assert.False(analysis.IsValid);
    }

    [Theory]
    [InlineData("pct=50")]
    [InlineData("rf=afrf")]
    [InlineData("ri=86400")]
    public void ObsoleteTag_IsInformational(string obsoleteTag)
    {
        var analysis = Analyse($"v=DMARC1; p=reject; {obsoleteTag}");

        var problem = Assert.Single(analysis.Problems);
        Assert.Equal(DmarcRecordProblemKind.ObsoleteTag, problem.Kind);
        Assert.Equal(obsoleteTag[..obsoleteTag.IndexOf('=')], problem.Tag);
        Assert.True(analysis.IsValid);
    }

    [Fact]
    public void PercentIsTheFirstValidPct()
    {
        Assert.Equal(50, Analyse("v=DMARC1; p=reject; pct=bogus; pct=50; pct=20").Percent);
        Assert.Null(Analyse("v=DMARC1; p=reject; pct=101").Percent);
    }

    [Fact]
    public void TestingMode_IsInformational()
    {
        var analysis = Analyse("v=DMARC1; p=reject; t=Y");

        var problem = Assert.Single(analysis.Problems);
        Assert.Equal(DmarcRecordProblemKind.TestingMode, problem.Kind);
        Assert.True(analysis.Testing);
        Assert.True(analysis.IsValid);
    }

    [Fact]
    public void RepeatedTestingTag_NoBeatsYes()
    {
        var analysis = Analyse("v=DMARC1; p=reject; t=n; t=y");

        Assert.False(analysis.Testing);
        var repeated = Assert.Single(analysis.Problems, problem => problem.Kind == DmarcRecordProblemKind.RepeatedTag);
        Assert.Equal("t", repeated.Tag);
        Assert.DoesNotContain(analysis.Problems, problem => problem.Kind == DmarcRecordProblemKind.TestingMode);
    }

    [Fact]
    public void RepeatedTestingTagWithSameValue_StaysTesting()
    {
        Assert.True(Analyse("v=DMARC1; p=reject; t=y; t=y").Testing);
    }

    [Fact]
    public void SubdomainAndNonExistentPoliciesInherit()
    {
        var inheriting = Analyse("v=DMARC1; p=reject");
        var withSubdomain = Analyse("v=DMARC1; p=reject; sp=quarantine");
        var withBoth = Analyse("v=DMARC1; p=reject; sp=quarantine; np=none");

        Assert.Equal(DmarcPolicyLevel.Reject, inheriting.EffectiveSubdomainPolicy);
        Assert.Equal(DmarcPolicyLevel.Reject, inheriting.EffectiveNonExistentPolicy);
        Assert.Equal(DmarcPolicyLevel.Quarantine, withSubdomain.EffectiveSubdomainPolicy);
        Assert.Equal(DmarcPolicyLevel.Quarantine, withSubdomain.EffectiveNonExistentPolicy);
        Assert.Equal(DmarcPolicyLevel.None, withBoth.EffectiveNonExistentPolicy);
    }

    [Fact]
    public void UpperCaseTagNamesAndValuesParse()
    {
        var analysis = Analyse("V=DMARC1; P=Reject; SP=Quarantine; NP=None; PCT=50; T=n");

        Assert.Equal(DmarcPolicyLevel.Reject, analysis.Policy);
        Assert.Equal(DmarcPolicyLevel.Quarantine, analysis.SubdomainPolicy);
        Assert.Equal(DmarcPolicyLevel.None, analysis.NonExistentPolicy);
        Assert.Equal(50, analysis.Percent);
        Assert.False(analysis.Testing);
    }

    [Theory]
    [InlineData(DmarcRecordProblemKind.VersionMissingOrNotFirst, true)]
    [InlineData(DmarcRecordProblemKind.InvalidNonExistentPolicy, true)]
    [InlineData(DmarcRecordProblemKind.ObsoleteTag, false)]
    [InlineData(DmarcRecordProblemKind.TestingMode, false)]
    public void IsBreaking_SplitsBreakingFromInformational(DmarcRecordProblemKind kind, bool expected)
    {
        Assert.Equal(expected, kind.IsBreaking());
    }

    [Fact]
    public void AnSpfRecordAtTheName_IsNotADmarcLookAlike()
    {
        var analysis = Analyse("v=spf1 -all");

        Assert.Empty(analysis.Records);
        Assert.Empty(analysis.Problems);
        Assert.Empty(analysis.IgnoredCandidates);
    }

    [Theory]
    [InlineData("v=dmarc1; p=reject")]
    [InlineData("v=DMARC2; p=reject")]
    [InlineData("v=DMARC1 p=reject")]
    [InlineData("p=reject; v=DMARC1")]
    public void ValuesThatLookLikeDmarc_AreAnalysedAsVersionProblems(string value)
    {
        var analysis = Analyse(value);

        Assert.Equal([value], analysis.Records);
        Assert.Contains(analysis.Problems, problem => problem.Kind == DmarcRecordProblemKind.VersionMissingOrNotFirst);
    }
}
