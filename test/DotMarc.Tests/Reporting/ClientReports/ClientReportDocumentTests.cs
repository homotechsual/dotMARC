using DotMarc.Portal;
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
    public void TheFileName_IsSafe()
    {
        var report = SampleReport() with { Brand = SampleReport().Brand with { Heading = "Aurora/Retail: \"Ltd\"" } };

        Assert.Equal("Aurora Retail Ltd email security report March 2026.pdf", ClientReportDocument.FileName(report));
    }
}
