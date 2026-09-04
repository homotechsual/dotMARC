using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DnsRecordLookupResultParsingTests
{
    [Fact]
    public void ParseTxtWithCnameDetection_ReturnsBothNull_WhenNoAnswers()
    {
        var result = DnsRecordLookupParsing.ParseTxtWithCnameDetection(null);

        Assert.Null(result.DirectValue);
        Assert.Null(result.DelegatedToCname);
    }

    [Fact]
    public void ParseTxtWithCnameDetection_ReturnsDirectValue_WhenPlainTxtRecord()
    {
        var answers = new[] { (Type: 16, Data: "\"v=DMARC1; p=reject;\"") };

        var result = DnsRecordLookupParsing.ParseTxtWithCnameDetection(answers);

        Assert.Equal("v=DMARC1; p=reject;", result.DirectValue);
        Assert.Null(result.DelegatedToCname);
    }

    [Fact]
    public void ParseTxtWithCnameDetection_DetectsDelegation_WhenCnameHopPrecedesTxt()
    {
        var answers = new[]
        {
            (Type: 5, Data: "_dmarc.example_com._d.easydmarc.pro."),
            (Type: 16, Data: "\"v=DMARC1;p=reject;\"")
        };

        var result = DnsRecordLookupParsing.ParseTxtWithCnameDetection(answers);

        Assert.Equal("v=DMARC1;p=reject;", result.DirectValue);
        Assert.Equal("_dmarc.example_com._d.easydmarc.pro.", result.DelegatedToCname);
    }

    [Fact]
    public void ParseTxtWithCnameDetection_JoinsSplitTxtStrings()
    {
        var answers = new[] { (Type: 16, Data: "\"v=DMARC1; \" \"p=reject;\"") };

        var result = DnsRecordLookupParsing.ParseTxtWithCnameDetection(answers);

        Assert.Equal("v=DMARC1; p=reject;", result.DirectValue);
    }
}
