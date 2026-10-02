using DotMarc.Data;
using DotMarc.Dns;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class DmarcPolicyTagsTests
{
    [Fact]
    public void Parse_ReadsPAndFillsSpAndPctFromTheirDefaults()
    {
        Assert.Equal(new DmarcPolicyTags(DmarcPolicyLevel.Reject, DmarcPolicyLevel.Reject, 100),
            DmarcPolicyTags.Parse("v=DMARC1; p=reject; rua=mailto:rua@example.com"));
    }

    [Fact]
    public void Parse_IgnoresCaseAndSpaces()
    {
        Assert.Equal(new DmarcPolicyTags(DmarcPolicyLevel.Quarantine, DmarcPolicyLevel.None, 50),
            DmarcPolicyTags.Parse("v=DMARC1;  P = Quarantine ;SP=NONE; pct= 50"));
    }

    [Theory]
    [InlineData("v=DMARC1; rua=mailto:rua@example.com")]
    [InlineData("v=DMARC1; p=bogus")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_IsNull_WithoutAValidP(string? record)
    {
        Assert.Null(DmarcPolicyTags.Parse(record));
    }

    [Theory]
    [InlineData("v=DMARC1; p=reject; sp=bogus", DmarcPolicyLevel.Reject)]
    [InlineData("v=DMARC1; p=none; sp=", DmarcPolicyLevel.None)]
    public void Parse_FallsBackToP_ForAnInvalidSp(string record, DmarcPolicyLevel expected)
    {
        Assert.Equal(expected, DmarcPolicyTags.Parse(record)!.SubdomainPolicy);
    }

    [Theory]
    [InlineData("pct=150")]
    [InlineData("pct=-1")]
    [InlineData("pct=abc")]
    [InlineData("pct=")]
    public void Parse_FallsBackTo100_ForAnInvalidPct(string pctTag)
    {
        Assert.Equal(100, DmarcPolicyTags.Parse($"v=DMARC1; p=reject; {pctTag}")!.Percent);
    }

    [Fact]
    public void Parse_UsesTheFirstOfARepeatedTag()
    {
        Assert.Equal(DmarcPolicyLevel.Quarantine, DmarcPolicyTags.Parse("v=DMARC1; p=quarantine; p=reject")!.Policy);
    }

    [Fact]
    public void Format_ReadsBackAsTheSamePolicy()
    {
        var tags = new DmarcPolicyTags(DmarcPolicyLevel.Quarantine, DmarcPolicyLevel.None, 25);

        Assert.Equal("p=quarantine; sp=none; pct=25", tags.Format());
        Assert.Equal(tags, DmarcPolicyTags.Parse(tags.Format()));
    }

    [Theory]
    [InlineData("p=quarantine; sp=reject; pct=100", true)]
    [InlineData("p=reject; sp=none; pct=100", true)]
    [InlineData("p=reject; sp=reject; pct=50", true)]
    [InlineData("p=reject; sp=reject; pct=100", false)]
    public void IsWeakerThan_ComparesEachTag(string current, bool weaker)
    {
        var baseline = new DmarcPolicyTags(DmarcPolicyLevel.Reject, DmarcPolicyLevel.Reject, 100);

        Assert.Equal(weaker, DmarcPolicyTags.Parse(current)!.IsWeakerThan(baseline));
    }

    [Fact]
    public void AStrongerPolicy_IsNotWeaker()
    {
        var baseline = new DmarcPolicyTags(DmarcPolicyLevel.None, DmarcPolicyLevel.None, 50);

        Assert.False(new DmarcPolicyTags(DmarcPolicyLevel.Reject, DmarcPolicyLevel.Reject, 100).IsWeakerThan(baseline));
    }

    [Fact]
    public void Of_ReadsADomainsStoredPolicy_OrNullWithout()
    {
        Assert.Equal(new DmarcPolicyTags(DmarcPolicyLevel.Quarantine, DmarcPolicyLevel.Quarantine, 100),
            DmarcPolicyTags.Of(new Domain { Name = "contoso.com", DmarcPolicy = DmarcPolicyLevel.Quarantine }));
        Assert.Null(DmarcPolicyTags.Of(new Domain { Name = "contoso.com" }));
    }
}
