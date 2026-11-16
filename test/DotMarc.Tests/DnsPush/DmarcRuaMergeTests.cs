// test/DotMarc.Tests/DnsPush/DmarcRuaMergeTests.cs
using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DmarcRuaMergeTests
{
    [Fact]
    public void TryMerge_PutsTheMailboxFirstAndKeepsAnotherAddress_PreservingOtherTags()
    {
        var result = DmarcRuaMerge.TryMerge("v=DMARC1; p=quarantine; rua=mailto:other@example.com; sp=reject", "correct@mjco.uk");

        Assert.Equal("v=DMARC1; p=quarantine; rua=mailto:correct@mjco.uk,mailto:other@example.com; sp=reject", result);
    }

    [Fact]
    public void TryMerge_ReplacesEveryAddress_WhenKeepingOthersIsOff()
    {
        var result = DmarcRuaMerge.TryMerge("v=DMARC1; p=reject; rua=mailto:other@example.com,mailto:correct@mjco.uk; pct=50", "correct@mjco.uk", keepOtherAddresses: false);

        Assert.Equal("v=DMARC1; p=reject; rua=mailto:correct@mjco.uk; pct=50", result);
    }

    [Fact]
    public void TryMerge_ListsEachAddressOnce_WhenTheMailboxIsAlreadyThere()
    {
        var result = DmarcRuaMerge.TryMerge("v=DMARC1; p=reject; rua=mailto:other@example.com,mailto:CORRECT@mjco.uk; pct=50", "correct@mjco.uk");

        Assert.Equal("v=DMARC1; p=reject; rua=mailto:correct@mjco.uk,mailto:other@example.com; pct=50", result);
    }

    [Fact]
    public void TryMerge_StopsAtTwoAddresses_DroppingTheLaterOnes()
    {
        var result = DmarcRuaMerge.TryMerge("v=DMARC1; p=none; rua=mailto:a@example.com,mailto:b@example.com,mailto:c@example.com", "correct@mjco.uk");

        Assert.Equal("v=DMARC1; p=none; rua=mailto:correct@mjco.uk,mailto:a@example.com", result);
    }

    [Fact]
    public void TryMerge_AddsMissingMailtoSchemes_AndKeepsSizeSuffixes()
    {
        var result = DmarcRuaMerge.TryMerge("v=DMARC1; p=none; rua=other@example.com!10m", "correct@mjco.uk");

        Assert.Equal("v=DMARC1; p=none; rua=mailto:correct@mjco.uk,mailto:other@example.com!10m", result);
    }

    [Fact]
    public void TryMerge_WritesTheAdditionalAddressSecond_AheadOfExistingOnes()
    {
        var result = DmarcRuaMerge.TryMerge("v=DMARC1; p=none; rua=mailto:old@example.com", "correct@mjco.uk", additionalAddress: "second@tool.example");

        Assert.Equal("v=DMARC1; p=none; rua=mailto:correct@mjco.uk,mailto:second@tool.example", result);
    }

    [Fact]
    public void TryMerge_WritesTheAdditionalAddressOnce_WhenAlreadyListed()
    {
        var result = DmarcRuaMerge.TryMerge("v=DMARC1; p=none; rua=mailto:second@tool.example", "correct@mjco.uk", additionalAddress: "Second@tool.example");

        Assert.Equal("v=DMARC1; p=none; rua=mailto:correct@mjco.uk,mailto:Second@tool.example", result);
    }

    [Fact]
    public void TryMerge_AppendsRuaTag_WhenNoneExists()
    {
        var result = DmarcRuaMerge.TryMerge("v=DMARC1; p=quarantine", "correct@mjco.uk");

        Assert.Equal("v=DMARC1; p=quarantine; rua=mailto:correct@mjco.uk", result);
    }

    [Fact]
    public void TryMerge_ReturnsNull_WhenExistingValueIsNotADmarcRecord()
    {
        var result = DmarcRuaMerge.TryMerge("some unrelated txt record", "correct@mjco.uk");

        Assert.Null(result);
    }

    [Fact]
    public void TryMerge_IsCaseInsensitive_OnTheVersionTag()
    {
        var result = DmarcRuaMerge.TryMerge("v=dmarc1; p=none", "correct@mjco.uk");

        Assert.Equal("v=dmarc1; p=none; rua=mailto:correct@mjco.uk", result);
    }

    [Fact]
    public void RuaTag_ListsTheMailboxThenTheAdditionalAddress()
    {
        Assert.Equal("rua=mailto:correct@mjco.uk", DmarcRuaMerge.RuaTag("correct@mjco.uk"));
        Assert.Equal("rua=mailto:correct@mjco.uk,mailto:second@tool.example", DmarcRuaMerge.RuaTag("correct@mjco.uk", "second@tool.example"));
    }
}
