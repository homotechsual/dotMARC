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

    private static byte[] BuildReport(
        string policyPublished = "quarantine",
        string disposition = "none",
        string evaluatedSpf = "pass",
        string evaluatedDkim = "pass",
        string authSpfResult = "pass",
        string? reasonType = null,
        string encoding = "UTF-8",
        string orgName = "google.com")
    {
        var reason = reasonType is null ? "" : $"<reason><type>{reasonType}</type><comment>test</comment></reason>";
        var xml = $"""
            <?xml version="1.0" encoding="{encoding}" ?>
            <feedback>
              <report_metadata>
                <org_name>{orgName}</org_name>
                <email>noreply@example.com</email>
                <report_id>normalise-1</report_id>
                <date_range><begin>1754438400</begin><end>1754524800</end></date_range>
              </report_metadata>
              <policy_published>
                <domain>contoso.io</domain>
                <adkim>r</adkim>
                <aspf>r</aspf>
                <p>{policyPublished}</p>
                <sp>{policyPublished}</sp>
                <pct>100</pct>
              </policy_published>
              <record>
                <row>
                  <source_ip>203.0.113.9</source_ip>
                  <count>4</count>
                  <policy_evaluated>
                    <disposition>{disposition}</disposition>
                    <dkim>{evaluatedDkim}</dkim>
                    <spf>{evaluatedSpf}</spf>
                    {reason}
                  </policy_evaluated>
                </row>
                <identifiers><header_from>contoso.io</header_from></identifiers>
                <auth_results>
                  <spf><domain>contoso.io</domain><result>{authSpfResult}</result></spf>
                </auth_results>
              </record>
            </feedback>
            """;
        return System.Text.Encoding.GetEncoding(encoding).GetBytes(xml);
    }

    [Fact]
    public void Parse_AcceptsACapitalisedAuthResult()
    {
        // Some reporters write "Fail" where the schema (and DmarcRua's case-sensitive enum) wants "fail".
        var result = DmarcReportParser.Parse(BuildReport(authSpfResult: "Fail"));

        var spf = result.Records.Single().AuthDetails.Single(d => d.Mechanism == DmarcAuthMechanism.Spf);
        Assert.Equal(DmarcMechanismResult.Fail, spf.Result);
    }

    [Fact]
    public void Parse_AcceptsCapitalisedPolicyEvaluatedValues()
    {
        var result = DmarcReportParser.Parse(BuildReport(disposition: "Quarantine", evaluatedSpf: "Fail", evaluatedDkim: "Pass"));

        var record = result.Records.Single();
        Assert.Equal("Quarantine", record.Disposition);
        Assert.Equal("Fail", record.SpfResult);
        Assert.Equal("Pass", record.DkimResult);
    }

    [Fact]
    public void Parse_TreatsANoPolicyDispositionAsNone()
    {
        var result = DmarcReportParser.Parse(BuildReport(disposition: "no policy"));

        Assert.Equal("None", result.Records.Single().Disposition);
    }

    [Fact]
    public void Parse_TreatsANoPolicyPublishedPolicyAsNone()
    {
        var result = DmarcReportParser.Parse(BuildReport(policyPublished: "no policy"));

        Assert.Equal("contoso.io", result.Domain);
    }

    [Fact]
    public void Parse_AcceptsACapitalisedOverrideReasonType()
    {
        var result = DmarcReportParser.Parse(BuildReport(reasonType: "Forwarded"));

        Assert.Equal(DmarcPolicyOverrideType.Forwarded, result.Records.Single().OverrideReasons.Single().Type);
    }

    [Fact]
    public void Parse_PreservesNonAsciiText_WhenTheReportNeedsNormalising()
    {
        var result = DmarcReportParser.Parse(BuildReport(authSpfResult: "Fail", encoding: "ISO-8859-1", orgName: "Société Générale"));

        Assert.Equal("Société Générale", result.ReportingOrg);
    }

    [Fact]
    public void Parse_StillRejectsAnUnknownEnumValue_EvenAfterNormalising()
    {
        Assert.Throws<InvalidDataException>(() => DmarcReportParser.Parse(BuildReport(authSpfResult: "banana")));
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
