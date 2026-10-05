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

    [Fact]
    public void FromPublished_PrefersTheCnameTarget()
    {
        var published = new DotMarc.DnsPush.DnsRecordLookupResult("v=DKIM1; p=KEY", "Selector1-Contoso-Com._domainkey.contoso.onmicrosoft.com.");

        Assert.Equal((DkimRecordType.Cname, "selector1-contoso-com._domainkey.contoso.onmicrosoft.com"), DkimRecordValue.FromPublished(published));
    }

    [Fact]
    public void FromPublished_UsesATxtValueWithAKey()
    {
        var published = new DotMarc.DnsPush.DnsRecordLookupResult("v=DKIM1; k=rsa; p=MIIB IjAN", null);

        Assert.Equal((DkimRecordType.Txt, "v=DKIM1; k=rsa; p=MIIBIjAN"), DkimRecordValue.FromPublished(published));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("v=spf1 -all")]
    public void FromPublished_IgnoresNothingOrAValueWithoutAKey(string? directValue)
    {
        Assert.Null(DkimRecordValue.FromPublished(new DotMarc.DnsPush.DnsRecordLookupResult(directValue, null)));
    }

    [Theory]
    [InlineData("selector1", "asylumjustice.org.uk", "selector1-asylumjustice-org-uk._domainkey.")]
    [InlineData("selector2", "contoso.com", "selector2-contoso-com._domainkey.")]
    [InlineData("google", "contoso.com", null)]
    public void Microsoft365TargetStart_FillsTheKnownPart(string selector, string domainName, string? expected)
    {
        Assert.Equal(expected, DkimRecordValue.Microsoft365TargetStart(selector, domainName));
    }

    [Fact]
    public void Microsoft365Targets_ReadsGetDkimSigningConfigOutput()
    {
        const string pasted = """
            Name           : contoso.com
            Enabled        : False
            Status         : CnameMissing
            Selector1CNAME : selector1-contoso-com._domainkey.contoso.n-v1.dkim.mail.microsoft
            Selector2CNAME : selector2-contoso-com._domainkey.contoso.n-v1.dkim.mail.microsoft
            """;

        var targets = DkimRecordValue.Microsoft365Targets(pasted);

        Assert.Equal("selector1-contoso-com._domainkey.contoso.n-v1.dkim.mail.microsoft", targets["selector1"]);
        Assert.Equal("selector2-contoso-com._domainkey.contoso.n-v1.dkim.mail.microsoft", targets["selector2"]);
    }

    [Fact]
    public void Microsoft365Targets_ReadsTheDefenderPortalsText_InEitherFormat()
    {
        const string pasted = """
            Publish CNAMEs
            Hostname: selector1._domainkey
            Points to address or value: Selector1-Contoso-Com._domainkey.contoso.onmicrosoft.com.
            Hostname: selector2._domainkey
            Points to address or value: selector2-contoso-com._domainkey.contoso.onmicrosoft.com
            """;

        var targets = DkimRecordValue.Microsoft365Targets(pasted);

        Assert.Equal(2, targets.Count);
        Assert.Equal("selector1-contoso-com._domainkey.contoso.onmicrosoft.com", targets["selector1"]);
    }

    [Fact]
    public void Microsoft365Targets_FindsNothingInUnrelatedText()
    {
        Assert.Empty(DkimRecordValue.Microsoft365Targets("selector1._domainkey CNAME missing"));
    }

    [Theory]
    [InlineData("selector1-contoso-com._domainkey.")]
    [InlineData("selector1-contoso-com._domainkey")]
    public void Validate_RefusesATargetThatStopsAtDomainkey(string value)
    {
        var problem = DkimRecordValue.Validate(DkimRecordType.Cname, DkimRecordValue.Normalize(DkimRecordType.Cname, value));

        Assert.NotNull(problem);
        Assert.Contains("_domainkey", problem);
    }
}
