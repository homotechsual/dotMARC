// test/DotMarc.Tests/DnsPush/DmarcRuaMergeTests.cs
using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DmarcRuaMergeTests
{
    [Fact]
    public void TryMerge_ReplacesAnExistingRuaTag_PreservingOtherTags()
    {
        var result = DmarcRuaMerge.TryMerge("v=DMARC1; p=quarantine; rua=mailto:wrong@example.com; sp=reject", "correct@mjco.uk");

        Assert.Equal("v=DMARC1; p=quarantine; rua=mailto:correct@mjco.uk; sp=reject", result);
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
}
