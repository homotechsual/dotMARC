using DotMarc.Data;
using DotMarc.Reporting;
using Xunit;

namespace DotMarc.Tests.Reporting;

public sealed class SenderIdentificationTests
{
    private const string HeaderFrom = "example.com";
    private static readonly IReadOnlyCollection<string> NoSpfIncludes = [];

    private static AuthDetailSummary Spf(string domain, DmarcMechanismResult result) => new(DmarcAuthMechanism.Spf, domain, result);

    private static AuthDetailSummary Dkim(string domain, DmarcMechanismResult result, string? selector = null) =>
        new(DmarcAuthMechanism.Dkim, domain, result, selector);

    private static ReportRecord Record(
        string ip, int messages, bool passing, DispositionResult disposition = DispositionResult.None, params AuthDetailSummary[] details)
    {
        var record = new ReportRecord
        {
            SourceIp = ip,
            MessageCount = messages,
            Disposition = disposition,
            SpfResult = passing ? AuthResult.Pass : AuthResult.Fail,
            DkimResult = AuthResult.Fail,
            HeaderFrom = HeaderFrom,
        };
        foreach (var detail in details)
        {
            record.AuthDetails.Add(new ReportRecordAuthDetail
            {
                Mechanism = detail.Mechanism, Domain = detail.Domain, Result = detail.Result, Selector = detail.Selector,
            });
        }
        return record;
    }

    private static Report ReportOf(params ReportRecord[] records) =>
        new() { ReportingOrg = "receiver.example", ReportId = Guid.NewGuid().ToString(), RawXml = "<feedback/>", Records = [.. records] };

    private static IReadOnlyDictionary<string, string?> Owners(params (string Ip, string? Owner)[] owners) =>
        owners.ToDictionary(entry => entry.Ip, entry => entry.Owner);

    [Theory]
    [InlineData(null, "fail", SenderFailureCause.DkimMissing)]
    [InlineData("misaligned", null, SenderFailureCause.DkimNotAligned)]
    [InlineData("failed", "misaligned", SenderFailureCause.SpfNotAligned)]
    [InlineData("misaligned", "misaligned", SenderFailureCause.Both)]
    [InlineData("failed", "failed", SenderFailureCause.Both)]
    [InlineData(null, null, SenderFailureCause.Both)]
    [InlineData("failed", null, SenderFailureCause.Both)]
    public void ClassifyFailureCause_FollowsTheTable(string? dkimOutcome, string? spfOutcome, SenderFailureCause expected)
    {
        // The theory's first column is DKIM, the second SPF: null = no check reported, "misaligned" = passed for another domain,
        // "failed"/"fail" = a real negative result.
        var details = new List<AuthDetailSummary>();
        if (dkimOutcome is not null)
        {
            details.Add(Dkim(dkimOutcome == "misaligned" ? "mcsv.net" : HeaderFrom, dkimOutcome == "misaligned" ? DmarcMechanismResult.Pass : DmarcMechanismResult.Fail));
        }
        if (spfOutcome is not null)
        {
            details.Add(Spf(spfOutcome == "misaligned" ? "bounce.mcsv.net" : HeaderFrom, spfOutcome == "misaligned" ? DmarcMechanismResult.Pass : DmarcMechanismResult.Fail));
        }

        Assert.Equal(expected, DomainStatistics.ClassifyFailureCause(details, HeaderFrom));
    }

    [Fact]
    public void ASourceWithPassingMailchimpDkimDomain_IsAttributedToMailchimp_WithItsVolumeShareAndPassRate()
    {
        var report = ReportOf(
            Record("198.51.100.1", 100, passing: true, details: Dkim("bounce.mcsv.net", DmarcMechanismResult.Pass)),
            Record("203.0.113.7", 100, passing: true));

        var result = SenderIdentification.Identify([report], Owners(), NoSpfIncludes);

        var mailchimp = Assert.Single(result.Services);
        Assert.Equal("Mailchimp", mailchimp.Name);
        Assert.Equal(100, mailchimp.Messages);
        Assert.Equal(0.5, mailchimp.Share, precision: 3);
        Assert.Equal(1.0, mailchimp.PassRate);
        Assert.Equal(0.9, mailchimp.Evidence!.Value, precision: 3);
        Assert.Equal(["198.51.100.1"], mailchimp.SourceIps);
        Assert.Contains("DKIM signed by bounce.mcsv.net and passed", mailchimp.EvidenceLines);
        Assert.NotNull(result.Unrecognised);
        Assert.Equal(100, result.Unrecognised!.Messages);
    }

    [Fact]
    public void AnAlignedMicrosoftSender_IsFoundThroughOwnerAndSelector_WhenAuthDomainsAreTheCustomersOwn()
    {
        var report = ReportOf(Record("40.107.0.1", 50, passing: true, details: Dkim(HeaderFrom, DmarcMechanismResult.Pass, "selector1")));

        var result = SenderIdentification.Identify([report], Owners(("40.107.0.1", "Microsoft Corporation")), NoSpfIncludes);

        var microsoft = Assert.Single(result.Services);
        Assert.Equal("Microsoft 365", microsoft.Name);
        Assert.True(microsoft.Evidence >= SenderScoring.StrongFrom);
        Assert.Null(result.Unrecognised);
    }

    [Fact]
    public void FailingMessages_AreGroupedPerService_WithTheDominantCauseAndFixNamingTheService()
    {
        var mailchimpMisaligned = Dkim("mcsv.net", DmarcMechanismResult.Pass);
        var report = ReportOf(
            Record("198.51.100.1", 60, passing: true, details: mailchimpMisaligned),
            Record("198.51.100.1", 40, passing: false, details: mailchimpMisaligned));

        var mailchimp = Assert.Single(SenderIdentification.Identify([report], Owners(), NoSpfIncludes).Services);

        Assert.Equal(100, mailchimp.Messages);
        Assert.Equal(60, mailchimp.Passing);
        Assert.Equal(40, mailchimp.Failing);
        Assert.Equal(0.6, mailchimp.PassRate!.Value, precision: 3);
        Assert.Equal(SenderFailureCause.DkimNotAligned, mailchimp.DominantCause);
        Assert.Contains("Mailchimp", mailchimp.Fix);
    }

    [Fact]
    public void ActedOn_CountsQuarantineAndRejectSeparatelyFromFailing()
    {
        var details = Dkim("mcsv.net", DmarcMechanismResult.Pass);
        var report = ReportOf(
            Record("198.51.100.1", 10, passing: false, DispositionResult.Reject, details),
            Record("198.51.100.1", 5, passing: false, DispositionResult.Quarantine, details),
            Record("198.51.100.1", 20, passing: false, DispositionResult.None, details));

        var mailchimp = Assert.Single(SenderIdentification.Identify([report], Owners(), NoSpfIncludes).Services);

        Assert.Equal(35, mailchimp.Failing);
        Assert.Equal(15, mailchimp.ActedOn);
    }

    [Fact]
    public void ASourceBelowThirtyPercent_GoesToUnrecognised_AndIsTheLastRow()
    {
        var report = ReportOf(
            Record("203.0.113.7", 500, passing: true),
            Record("198.51.100.1", 10, passing: true, details: Dkim("mcsv.net", DmarcMechanismResult.Pass)));

        var result = SenderIdentification.Identify([report], Owners(), ["Zoho"]);

        Assert.Equal(["Mailchimp"], result.Services.Select(row => row.Name));
        Assert.True(result.Unrecognised!.IsUnrecognised);
        Assert.Equal(500, result.Unrecognised.Messages);
        Assert.Equal(["203.0.113.7"], result.Unrecognised.SourceIps);
    }

    [Fact]
    public void AServiceRowEvidence_IsTheHighestSourceScore_AndServiceByIpMapsEveryAttributedIp()
    {
        var report = ReportOf(
            Record("198.51.100.1", 10, passing: true, details: Dkim("mcsv.net", DmarcMechanismResult.Pass)),
            Record("198.51.100.2", 10, passing: true));

        var result = SenderIdentification.Identify([report], Owners(("198.51.100.2", "The Rocket Science Group")), NoSpfIncludes);

        var mailchimp = Assert.Single(result.Services);
        Assert.Equal(0.9, mailchimp.Evidence!.Value, precision: 3);
        Assert.Equal("Mailchimp", result.ServiceByIp["198.51.100.1"]);
        Assert.Equal("Mailchimp", result.ServiceByIp["198.51.100.2"]);
    }

    [Fact]
    public void NoReports_GivesNoRowsAndNoUnrecognised()
    {
        var result = SenderIdentification.Identify([], Owners(), NoSpfIncludes);

        Assert.Empty(result.Services);
        Assert.Null(result.Unrecognised);
        Assert.Empty(result.ServiceByIp);
    }

    [Fact]
    public void OnlyPassingMail_HasZeroFailingAndNoFix()
    {
        var report = ReportOf(Record("198.51.100.1", 10, passing: true, details: Dkim("mcsv.net", DmarcMechanismResult.Pass)));

        var mailchimp = Assert.Single(SenderIdentification.Identify([report], Owners(), NoSpfIncludes).Services);

        Assert.Equal(0, mailchimp.Failing);
        Assert.Null(mailchimp.Fix);
        Assert.Null(mailchimp.DominantCause);
    }

    [Fact]
    public void RecordsWithoutAuthDetails_DoNotThrow_AndFallBackToBoth()
    {
        var report = ReportOf(Record("142.250.0.1", 30, passing: false));

        var google = Assert.Single(SenderIdentification.Identify([report], Owners(("142.250.0.1", "Google LLC")), NoSpfIncludes).Services);

        Assert.Equal("Google Workspace", google.Name);
        Assert.Equal(SenderFailureCause.Both, google.DominantCause);
    }

    [Fact]
    public void SharesAcrossRowsAddUpToOne()
    {
        var report = ReportOf(
            Record("198.51.100.1", 30, passing: true, details: Dkim("mcsv.net", DmarcMechanismResult.Pass)),
            Record("142.250.0.1", 50, passing: true, details: Dkim("gmail.com", DmarcMechanismResult.Pass)),
            Record("203.0.113.7", 20, passing: true));

        var result = SenderIdentification.Identify([report], Owners(), NoSpfIncludes);

        var total = result.Services.Sum(row => row.Share) + result.Unrecognised!.Share;
        Assert.Equal(1.0, total, precision: 6);
        Assert.Equal(["Google Workspace", "Mailchimp"], result.Services.Select(row => row.Name));
    }

    [Fact]
    public void RecordsCanBePassedDirectly_WithoutWrappingThemInAReport()
    {
        var record = Record("198.51.100.1", 10, passing: true, details: Dkim("mcsv.net", DmarcMechanismResult.Pass));

        var result = SenderIdentification.IdentifyRecords([record], Owners(), NoSpfIncludes);

        Assert.Equal("Mailchimp", result.ServiceByIp["198.51.100.1"]);
    }

    [Fact]
    public void FixText_StatesTheCauseFirst_ThenTheFix_NamingTheService_WithoutEmDashes()
    {
        foreach (var cause in Enum.GetValues<SenderFailureCause>())
        {
            var text = SenderIdentification.FixText("Mailchimp", cause);

            Assert.Contains("Mailchimp", text);
            Assert.DoesNotContain('—', text);
        }
    }
}
