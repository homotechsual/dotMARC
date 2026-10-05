using DotMarc.Data;
using DotMarc.Dns;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class DkimRecordValueTests
{
    [Theory]
    [InlineData("\"v=DKIM1; k=rsa; \" \"p=MIIBIjAN\"", "v=DKIM1; k=rsa; p=MIIBIjAN")]
    [InlineData("v=DKIM1;  k=rsa;\r\n p=MIIB\nIjAN", "v=DKIM1; k=rsa; p=MIIBIjAN")]
    [InlineData("  v=DKIM1; p=ABC  ", "v=DKIM1; p=ABC")]
    public void Normalize_Txt_JoinsQuotedChunksAndLineBreaks(string pasted, string expected)
    {
        Assert.Equal(expected, DkimRecordValue.Normalize(DkimRecordType.Txt, pasted));
    }

    [Theory]
    [InlineData("selector1-contoso-com._domainkey.Contoso.onmicrosoft.com.", "selector1-contoso-com._domainkey.contoso.onmicrosoft.com")]
    [InlineData(" fm1.contoso.com.dkim.fmhosted.com ", "fm1.contoso.com.dkim.fmhosted.com")]
    public void Normalize_Cname_LowersAndDropsTheTrailingDot(string pasted, string expected)
    {
        Assert.Equal(expected, DkimRecordValue.Normalize(DkimRecordType.Cname, pasted));
    }

    [Theory]
    [InlineData(DkimRecordType.Cname, "selector1-contoso-com._domainkey.contoso.onmicrosoft.com", true)]
    [InlineData(DkimRecordType.Cname, "not a host", false)]
    [InlineData(DkimRecordType.Cname, "localhost", false)]
    [InlineData(DkimRecordType.Txt, "v=DKIM1; k=rsa; p=MIIBIjAN", true)]
    [InlineData(DkimRecordType.Txt, "v=DKIM1; k=rsa;", false)]
    [InlineData(DkimRecordType.Txt, "v=DKIM1; p=", false)]
    public void Validate_AcceptsHostNamesAndKeys(DkimRecordType type, string value, bool valid)
    {
        Assert.Equal(valid, DkimRecordValue.Validate(type, value) is null);
    }

    [Fact]
    public void PublicKey_IgnoresWhitespaceInsideTheKey()
    {
        Assert.Equal("MIIBIjAN", DkimRecordValue.PublicKey("v=DKIM1; k=rsa; p=MIIB IjAN"));
        Assert.Null(DkimRecordValue.PublicKey("v=DKIM1; k=rsa"));
    }

    [Theory]
    [InlineData("fm1", "fm1.contoso.com.dkim.fmhosted.com")]
    [InlineData("fm3", "fm3.contoso.com.dkim.fmhosted.com")]
    [InlineData("selector1", null)]
    public void FastmailTarget_IsPredictable(string selector, string? expected)
    {
        Assert.Equal(expected, DkimRecordValue.FastmailTarget(selector, "contoso.com"));
    }
}
