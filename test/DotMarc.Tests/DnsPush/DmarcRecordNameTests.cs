using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public class DmarcRecordNameTests
{
    [Theory]
    [InlineData("_dmarc.example.com", true)]
    [InlineData("_DMARC.Example.com", true)]
    [InlineData("example.com", false)]
    [InlineData("_dmarcx.example.com", false)]
    public void IsDmarcName_MatchesTheDmarcLabelOnly(string name, bool expected)
    {
        Assert.Equal(expected, DmarcRecordName.IsDmarcName(name));
    }
}
