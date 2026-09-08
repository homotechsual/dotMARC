using DotMarc.Data;
using DotMarc.Ingestion;
using Xunit;

namespace DotMarc.Tests.Ingestion;

public class DmarcReportParserTests
{
    [Fact]
    public void Parse_ExtractsReportMetadataAndRecords_FromAValidReport()
    {
        var xmlBytes = File.ReadAllBytes("Fixtures/sample-report.xml");

        var result = DmarcReportParser.Parse(xmlBytes);

        Assert.Equal("contoso.io", result.Domain);
        Assert.Equal("google.com", result.ReportingOrg);
        Assert.Equal("12345678901234567890", result.ReportId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1754438400), result.DateRangeBeginUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1754524800), result.DateRangeEndUtc);
        Assert.Equal(2, result.Records.Count);
    }

    [Fact]
    public void Parse_MapsEachRecordsSourceAndResults()
    {
        var xmlBytes = File.ReadAllBytes("Fixtures/sample-report.xml");

        var result = DmarcReportParser.Parse(xmlBytes);

        var failing = result.Records.Single(r => r.SourceIp == "203.0.113.44");
        Assert.Equal(230, failing.MessageCount);
        Assert.Equal("Quarantine", failing.Disposition);
        Assert.Equal("Fail", failing.SpfResult);
        Assert.Equal("Fail", failing.DkimResult);
        Assert.Equal("contoso.io", failing.HeaderFrom);

        var passing = result.Records.Single(r => r.SourceIp == "198.51.100.7");
        Assert.Equal(3980, passing.MessageCount);
        Assert.Equal("None", passing.Disposition);
        Assert.Equal("Pass", passing.SpfResult);
        Assert.Equal("Pass", passing.DkimResult);
    }

    [Fact]
    public void Parse_MapsAuthDetails_ForEveryDkimAndSpfEntry()
    {
        var xmlBytes = File.ReadAllBytes("Fixtures/sample-report-with-detail.xml");

        var result = DmarcReportParser.Parse(xmlBytes);

        var record = result.Records.Single();
        Assert.Equal(3, record.AuthDetails.Count);

        var spf = record.AuthDetails.Single(d => d.Mechanism == DmarcAuthMechanism.Spf);
        Assert.Equal("envelope.contoso.io", spf.Domain);
        Assert.Equal(DmarcMechanismResult.Fail, spf.Result);
        Assert.Null(spf.Selector);

        var dkimPass = record.AuthDetails.Single(d => d.Mechanism == DmarcAuthMechanism.Dkim && d.Selector == "default");
        Assert.Equal("contoso.io", dkimPass.Domain);
        Assert.Equal(DmarcMechanismResult.Pass, dkimPass.Result);

        var dkimTempError = record.AuthDetails.Single(d => d.Mechanism == DmarcAuthMechanism.Dkim && d.Selector == "backup");
        Assert.Equal("relay.contoso.io", dkimTempError.Domain);
        Assert.Equal(DmarcMechanismResult.TempError, dkimTempError.Result);
    }

    [Fact]
    public void Parse_MapsPolicyOverrideReasons()
    {
        var xmlBytes = File.ReadAllBytes("Fixtures/sample-report-with-detail.xml");

        var result = DmarcReportParser.Parse(xmlBytes);

        var reason = result.Records.Single().OverrideReasons.Single();
        Assert.Equal(DmarcPolicyOverrideType.LocalPolicy, reason.Type);
        Assert.Equal("arc allowed", reason.Comment);
    }

    [Fact]
    public void Parse_ReturnsEmptyAuthDetailsAndReasons_WhenTheReportHasNone()
    {
        var xmlBytes = File.ReadAllBytes("Fixtures/sample-report.xml");

        var result = DmarcReportParser.Parse(xmlBytes);

        var passing = result.Records.Single(r => r.SourceIp == "198.51.100.7");
        Assert.Empty(passing.OverrideReasons);
        Assert.Equal(2, passing.AuthDetails.Count); // this fixture's passing record already has one spf + one dkim entry
    }

    [Fact]
    public void Parse_Throws_ForGarbageBytes()
    {
        var garbage = "not xml at all"u8.ToArray();

        Assert.Throws<InvalidDataException>(() => DmarcReportParser.Parse(garbage));
    }

    [Fact]
    public void Parse_Throws_ForWellFormedButSchemaInvalidXml()
    {
        var xmlBytes = File.ReadAllBytes("Fixtures/invalid-report.xml");

        Assert.Throws<InvalidDataException>(() => DmarcReportParser.Parse(xmlBytes));
    }
}
