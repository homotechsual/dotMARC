using DotMarc.Dns;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class SpfRecordTests
{
    [Theory]
    [InlineData("v=spf1", true)]
    [InlineData("v=spf1 -all", true)]
    [InlineData("V=SPF1 ~all", true)]
    [InlineData("v=spf10 -all", false)]
    [InlineData("v=spf2.0/pra -all", false)]
    [InlineData("google-site-verification=abc", false)]
    [InlineData(null, false)]
    public void IsSpf_RecognisesOnlyVersion1Records(string? txt, bool expected)
    {
        Assert.Equal(expected, SpfRecord.IsSpf(txt));
    }

    [Fact]
    public void Parse_ReadsTermsInOrder_WithQualifiersNamesAndArguments()
    {
        var record = SpfRecord.Parse("v=spf1 ip4:192.0.2.0/24 include:_spf.google.com -include:bad.example a mx/24 ~all");

        Assert.Equal(
            [("ip4", '+', ":192.0.2.0/24"), ("include", '+', ":_spf.google.com"), ("include", '-', ":bad.example"), ("a", '+', ""), ("mx", '+', "/24"), ("all", '~', "")],
            record.Terms.Select(term => (term.Name, term.Qualifier, term.Argument)));
        Assert.All(record.Terms, term => Assert.Equal(SpfTermKind.Mechanism, term.Kind));
    }

    [Fact]
    public void Parse_KeepsModifiersAndUnknownTermsExactly()
    {
        var record = SpfRecord.Parse("v=spf1 redirect=_spf.example.com exp=explain.example.com foo:bar");

        Assert.Equal([SpfTermKind.Modifier, SpfTermKind.Modifier, SpfTermKind.Unknown], record.Terms.Select(term => term.Kind));
        Assert.True(record.Terms[0].IsRedirect);
        Assert.Equal("_spf.example.com", record.Terms[0].Target);
        Assert.Equal("v=spf1 redirect=_spf.example.com exp=explain.example.com foo:bar", record.Format());
    }

    [Fact]
    public void Format_UsesSingleSpaces()
    {
        Assert.Equal("v=spf1 include:a.example ~all", SpfRecord.Parse("v=spf1  include:a.example   ~all").Format());
    }

    [Fact]
    public void MechanismNames_IgnoreCase()
    {
        var record = SpfRecord.Parse("v=spf1 INCLUDE:a.example ~ALL");

        Assert.Equal(["a.example"], record.Includes);
        Assert.Equal('~', record.AllTerm!.Qualifier);
    }

    [Theory]
    [InlineData("include:a.example", true)]
    [InlineData("a", true)]
    [InlineData("mx/24", true)]
    [InlineData("ptr", true)]
    [InlineData("exists:%{i}.example", true)]
    [InlineData("redirect=a.example", true)]
    [InlineData("ip4:192.0.2.1", false)]
    [InlineData("ip6:2001:db8::1", false)]
    [InlineData("-all", false)]
    [InlineData("exp=explain.example", false)]
    public void CostsLookup_FollowsRfc7208(string text, bool expected)
    {
        Assert.Equal(expected, SpfTerm.Parse(text).CostsLookup);
    }

    [Theory]
    [InlineData("v=spf1 ip4:192.0.2.1 ~all", "v=spf1 ip4:192.0.2.1 include:new.example ~all")]
    [InlineData("v=spf1 include:a.example redirect=b.example", "v=spf1 include:a.example include:new.example redirect=b.example")]
    [InlineData("v=spf1 ip4:192.0.2.1", "v=spf1 ip4:192.0.2.1 include:new.example")]
    [InlineData("v=spf1 include:NEW.example ~all", "v=spf1 include:NEW.example ~all")]
    public void WithInclude_AddsBeforeTheEnding_AndNeverTwice(string record, string expected)
    {
        Assert.Equal(expected, SpfRecord.Parse(record).WithInclude("new.example").Format());
    }

    [Fact]
    public void WithoutInclude_RemovesOnlyThatInclude()
    {
        Assert.Equal("v=spf1 include:keep.example ~all",
            SpfRecord.Parse("v=spf1 include:keep.example include:OLD.example ~all").WithoutInclude("old.example").Format());
    }

    [Theory]
    [InlineData("v=spf1 a +all", '~', "v=spf1 a ~all")]
    [InlineData("v=spf1 include:a.example redirect=b.example", '-', "v=spf1 include:a.example include:b.example -all")]
    [InlineData("v=spf1 redirect=_spf.provider.example", '~', "v=spf1 include:_spf.provider.example ~all")]
    [InlineData("v=spf1 include:a.example", '~', "v=spf1 include:a.example ~all")]
    public void WithAll_SetsTheEnding_AndTurnsARedirectIntoAnInclude(string record, char qualifier, string expected)
    {
        Assert.Equal(expected, SpfRecord.Parse(record).WithAll(qualifier).Format());
    }

    [Theory]
    [InlineData(new[] { "v=spf1 include:a.example ~all", "v=spf1 include:b.example include:a.example -all" }, "v=spf1 include:a.example include:b.example -all")]
    [InlineData(new[] { "v=spf1 ip4:192.0.2.1", "v=spf1 redirect=x.example" }, "v=spf1 ip4:192.0.2.1 redirect=x.example")]
    [InlineData(new[] { "v=spf1 redirect=x.example", "v=spf1 ?all" }, "v=spf1 include:x.example ?all")]
    [InlineData(new[] { "v=spf1 a exp=one.example ~all", "v=spf1 mx exp=two.example ~all" }, "v=spf1 a exp=one.example mx ~all")]
    [InlineData(new[] { "v=spf1 +all", "v=spf1 ~all" }, "v=spf1 ~all")]
    public void Merge_CombinesTermsInOrder_WithTheStrictestEnding(string[] records, string expected)
    {
        Assert.Equal(expected, SpfRecord.Merge(records.Select(SpfRecord.Parse).ToList()).Format());
    }

    [Theory]
    [InlineData("v=spf1 -all", true)]
    [InlineData("v=spf1 ~all", false)]
    [InlineData("v=spf1 a -all", false)]
    public void IsNullRecord_IsMinusAllAlone(string record, bool expected)
    {
        Assert.Equal(expected, SpfRecord.Parse(record).IsNullRecord);
    }
}
