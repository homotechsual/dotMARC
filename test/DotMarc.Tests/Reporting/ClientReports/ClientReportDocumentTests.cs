using System.Globalization;
using DotMarc.Portal;
using DotMarc.Reporting;
using DotMarc.Reporting.ClientReports;
using MudBlazor;
using PdfSharp.Pdf.IO;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

public sealed class ClientReportDocumentTests
{
    internal static ClientReport SampleReport(byte[]? logo = null, IReadOnlyList<ClientReportDomain>? domains = null) => new(
        new ResolvedBrand("Nova MSP", "Aurora Retail Ltd", "#0B5FFF", "#FF6B00", null, null, "help@nova-msp.example", null, null, null),
        logo, "Aurora Retail", new ReportPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), ReportPeriodKind.Month), "Europe/London",
        "1 of 2 domains are fully protected. 1 needs attention.",
        domains ??
        [
            new("aurora-retail.example", new PortalDomainStatus(PortalHealth.NeedsAttention, ["SPF: Missing"]), 1200, 0.95, 0.9,
                [0.9, null, 1.0], "Mail that fails DMARC is rejected.", [new HealthRow("SPF", "Missing", Color.Error)],
                [new ClientReportSender("203.0.113.10", "Google LLC", 1000, 990, 10, 0.83)], new ReceiverActions(1100, 60, 40, 5),
                [new ClientReportAlert("SPF record broken", new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero), null)]),
            new("shop.aurora-retail.example", new PortalDomainStatus(PortalHealth.Protected, []), 300, 1.0, null,
                [1.0, 1.0, 1.0], "Mail that fails DMARC is rejected.", [], [], new ReceiverActions(300, 0, 0, 0), []),
        ],
        ["Publish an SPF record for aurora-retail.example."], new DateTimeOffset(2026, 4, 1, 5, 0, 0, TimeSpan.Zero));

    [Fact]
    public void TheDocument_SaysWhatTheReportSays()
    {
        var text = MigraDocText.Of(ClientReportDocument.Build(SampleReport()));

        Assert.Contains("Aurora Retail Ltd", text);
        Assert.Contains("March 2026", text);
        Assert.Contains("1 March 2026 to 31 March 2026", text);
        Assert.Contains("1 of 2 domains are fully protected. 1 needs attention.", text);
        Assert.Contains("aurora-retail.example", text);
        Assert.Contains("shop.aurora-retail.example", text);
        Assert.Contains("+5.0 pts", text);
        Assert.Contains("Google LLC", text);
        Assert.Contains("Publish an SPF record for aurora-retail.example.", text);
        Assert.Contains("SPF record broken", text);
        Assert.Contains("help@nova-msp.example", text);
    }

    public static TheoryData<long, long, long, string[]> SentenceCases => new()
    {
        { 120, 0, 0, ["120 messages from senders you haven't authorised were blocked."] },
        { 1, 0, 0, ["1 message from senders you haven't authorised was blocked."] },
        { 0, 40, 40, ["40 messages from senders you haven't authorised were still delivered. Moving the DMARC policy to quarantine or reject would block them."] },
        { 10, 5, 5, ["10 messages from senders you haven't authorised were blocked.", "5 messages from senders you haven't authorised were still delivered. Moving the DMARC policy to quarantine or reject would block them."] },
        { 0, 9, 0, ["9 messages from senders you haven't authorised were delivered. They pass DMARC, so we are checking whether they should be authorised."] },
        { 0, 0, 0, Array.Empty<string>() },
    };

    [Theory]
    [MemberData(nameof(SentenceCases))]
    public void UnauthorisedSentences_SayWhatWasBlockedAndWhatWasDelivered(long blocked, long delivered, long deliveredFailing, string[] expected) =>
        Assert.Equal(expected, ClientReportDocument.UnauthorisedSentences(new SenderDecisionSummary(0, blocked, delivered, deliveredFailing), CultureInfo.InvariantCulture));

    [Fact]
    public void TheDocument_ShowsTheSentencesOnlyForADomainWithUnauthorisedMail()
    {
        var domain = (SampleReport().Domains[1]) with { Senders = new SenderDecisionSummary(0, 12, 0, 0) };
        var quiet = SampleReport().Domains[0] with { Senders = new SenderDecisionSummary(2, 0, 0, 0) };

        var text = MigraDocText.Of(ClientReportDocument.Build(SampleReport(domains: [quiet, domain])));

        Assert.Equal(1, text.Split("you haven't authorised").Length - 1);
    }

    [Fact]
    public void ASenderWithAKnownService_IsListedByServiceThenIp()
    {
        var report = SampleReport(domains:
        [
            new("aurora-retail.example", new PortalDomainStatus(PortalHealth.Protected, []), 100, 1.0, null, [1.0], "Mail that fails DMARC is rejected.", [],
                [new ClientReportSender("198.51.100.7", "Rocket Science Group LLC", 100, 100, 0, 1.0, "Mailchimp")], new ReceiverActions(100, 0, 0, 0), []),
        ]);

        Assert.Contains("Mailchimp (198.51.100.7)", MigraDocText.Of(ClientReportDocument.Build(report)));
    }

    [Fact]
    public void AGroupWithNoDomains_StillRenders_AndSaysSo()
    {
        var report = SampleReport(domains: []) with { Verdict = "There are no domains in this report.", NextSteps = ClientReportNextSteps.For([], []) };

        var pdf = ClientReportDocument.Render(report);

        Assert.Contains("There are no domains in this report.", MigraDocText.Of(ClientReportDocument.Build(report)));
        Assert.Equal(1, PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import).PageCount);
    }

    [Fact]
    public void ThePdf_IsAPdf_WithItsFontEmbedded()
    {
        var pdf = ClientReportDocument.Render(SampleReport());

        Assert.Equal("%PDF-"u8.ToArray(), pdf[..5]);
        Assert.Contains("Roboto", System.Text.Encoding.Latin1.GetString(pdf));
    }

    [Fact]
    public void APngLogo_IsDrawn_AndWithoutOneTheProductNameIs()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        var withLogo = ClientReportDocument.Build(SampleReport(logo: png));
        var withoutLogo = MigraDocText.Of(ClientReportDocument.Build(SampleReport()));

        Assert.Contains(withLogo.Sections[0].Elements.OfType<MigraDoc.DocumentObjectModel.Shapes.Image>(), _ => true);
        Assert.Contains("Nova MSP", withoutLogo);
        ClientReportDocument.Render(SampleReport(logo: png)); // renders without throwing
    }

    [Fact]
    public void AlertDates_AreInTheReportsTimeZone()
    {
        var report = SampleReport() with { TimeZoneId = "Australia/Sydney" };

        // 13:30 UTC is already 00:30 the next day in Sydney (UTC+11 in March), so both dates move on a day.
        var withAlert = report with
        {
            Domains = [report.Domains[0] with { Alerts = [new ClientReportAlert("SPF record broken", new DateTimeOffset(2026, 3, 2, 13, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 4, 13, 30, 0, TimeSpan.Zero))] }],
        };

        Assert.Contains("SPF record broken: raised 3 March 2026, resolved 5 March 2026.", MigraDocText.Of(ClientReportDocument.Build(withAlert)));
    }

    [Fact]
    public void Numbers_FollowTheReportsNumberFormat()
    {
        var british = MigraDocText.Of(ClientReportDocument.Build(SampleReport() with { NumberFormat = "en-GB" }));
        var german = MigraDocText.Of(ClientReportDocument.Build(SampleReport() with { NumberFormat = "de-DE" }));

        Assert.Contains("1,200", british);
        Assert.Contains("95.0%", british);
        Assert.Contains("+5.0 pts", british);
        Assert.Contains("1.200", german);
        Assert.Contains("95,0 %", german);
        Assert.Contains("+5,0 pts", german);
    }

    [Fact]
    public void TheFileName_IsSafe()
    {
        var report = SampleReport() with { Brand = SampleReport().Brand with { Heading = "Aurora/Retail: \"Ltd\"" } };

        Assert.Equal("Aurora Retail Ltd email security report March 2026.pdf", ClientReportDocument.FileName(report));
    }

    [Fact]
    public void SectionHeadings_AndTableHeaders_UseTheSecondaryColour()
    {
        var document = ClientReportDocument.Build(SampleReport());
        var secondary = new MigraDoc.DocumentObjectModel.Color(0xFF, 0x6B, 0x00);

        Assert.Equal(secondary, document.Styles[MigraDoc.DocumentObjectModel.StyleNames.Heading2]!.Font.Color);
        var headers = document.Sections[0].Elements.OfType<MigraDoc.DocumentObjectModel.Tables.Table>().Select(table => table.Rows[0]).ToList();
        Assert.NotEmpty(headers);
        Assert.All(headers, header => Assert.Equal(secondary, header.Shading.Color));
    }
}
