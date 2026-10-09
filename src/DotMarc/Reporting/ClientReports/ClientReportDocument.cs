using System.Globalization;
using System.Text.RegularExpressions;
using DotMarc.Portal;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Shapes.Charts;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

namespace DotMarc.Reporting.ClientReports;

/// <summary>Lays a report out as a MigraDoc document (A4, the brand's colours, the bundled Roboto) and renders it to
/// PDF. Building and rendering are separate so tests can read the document's text.</summary>
public static partial class ClientReportDocument
{
    private const int ChartedDomainLimit = 6;
    private static readonly string[] SeriesColours = ["#0B5FFF", "#E3594F", "#2E7D32", "#F9A825", "#6A1B9A", "#00838F"];

    public static byte[] Render(ClientReport report)
    {
        ReportFontResolver.EnsureInstalled();
        var renderer = new PdfDocumentRenderer { Document = Build(report) };
        renderer.RenderDocument();
        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    public static string FileName(ClientReport report)
    {
        var safe = SpacesPattern().Replace(UnsafePattern().Replace($"{report.Brand.Heading} email security report {report.Period.Label}", " "), " ").Trim();
        return $"{safe}.pdf";
    }

    public static Document Build(ClientReport report)
    {
        var primary = Hex(report.Brand.PrimaryColour);
        var document = new Document();
        document.Info.Title = $"{report.Brand.Heading} email security report: {report.Period.Label}";
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = ReportFontResolver.FamilyName;
        normal.Font.Size = 10;
        document.Styles[StyleNames.Heading1]!.Font.Size = 20;
        document.Styles[StyleNames.Heading1]!.Font.Bold = true;
        document.Styles[StyleNames.Heading1]!.Font.Color = primary;
        document.Styles[StyleNames.Heading2]!.Font.Size = 14;
        document.Styles[StyleNames.Heading2]!.Font.Bold = true;
        document.Styles[StyleNames.Heading2]!.ParagraphFormat.SpaceBefore = Unit.FromPoint(14);
        document.Styles[StyleNames.Heading2]!.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);

        var section = document.AddSection();
        section.PageSetup = document.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(2);
        section.Footers.Primary.AddParagraph(Footer(report)).Format.Font.Size = 8;

        AddCover(section, report, primary);
        if (report.Domains.Count > 0)
        {
            AddSummary(section, report);
            AddTrend(section, report);
            foreach (var domain in report.Domains)
            {
                AddDomain(section, domain);
            }
        }

        section.AddParagraph("What to do next", StyleNames.Heading2);
        foreach (var step in report.NextSteps)
        {
            var paragraph = section.AddParagraph();
            paragraph.Format.LeftIndent = Unit.FromCentimeter(0.4);
            paragraph.AddText("• " + step);
        }

        return document;
    }

    private static string Footer(ClientReport report)
    {
        var contact = new[] { report.Brand.SupportEmail, report.Brand.SupportUrl, report.Brand.SupportPhone }.OfType<string>();
        var parts = new[] { report.Brand.ProductName }.Concat(contact).Append($"Times in {report.TimeZoneId}");
        return string.Join("  |  ", parts);
    }

    private static void AddCover(Section section, ClientReport report, Color primary)
    {
        if (report.Logo is { } logo)
        {
            var image = section.AddImage("base64:" + Convert.ToBase64String(logo));
            image.Height = Unit.FromCentimeter(1.5);
            image.LockAspectRatio = true;
        }
        else
        {
            var name = section.AddParagraph(report.Brand.ProductName);
            name.Format.Font.Size = 14;
            name.Format.Font.Bold = true;
            name.Format.Font.Color = primary;
        }

        section.AddParagraph(report.Brand.Heading, StyleNames.Heading1).Format.SpaceBefore = Unit.FromPoint(12);
        section.AddParagraph($"Email security report: {report.Period.Label}").Format.Font.Size = 14;
        section.AddParagraph($"{ReportPeriod.Format(report.Period.Start)} to {ReportPeriod.Format(report.Period.End)}").Format.Font.Color = Colors.Gray;
        var verdict = section.AddParagraph(report.Verdict);
        verdict.Format.SpaceBefore = Unit.FromPoint(12);
        verdict.Format.Font.Size = 12;
        verdict.Format.Font.Bold = true;
    }

    private static void AddSummary(Section section, ClientReport report)
    {
        section.AddParagraph("Summary", StyleNames.Heading2);
        var table = NewTable(section, ("Domain", 6.0), ("Status", 3.2), ("Messages", 2.4), ("Pass rate", 2.2), ("Change", 2.2));
        foreach (var domain in report.Domains)
        {
            AddRow(table, domain.Name, StatusText(domain.Status.Health), Count(domain.Messages), Percent(domain.PassRate), domain.ChangeText);
            table.Rows[^1].Cells[1].Format.Font.Color = StatusColour(domain.Status.Health);
        }
    }

    private static void AddTrend(Section section, ClientReport report)
    {
        section.AddParagraph("Pass rate trend", StyleNames.Heading2);
        var charted = report.Domains.OrderByDescending(domain => domain.Messages).Take(ChartedDomainLimit).ToList();
        var chart = new Chart(ChartType.Line)
        {
            Width = Unit.FromCentimeter(16),
            Height = Unit.FromCentimeter(6),
            DisplayBlanksAs = BlankType.NotPlotted,
        };
        chart.YAxis.MinimumScale = 0;
        chart.YAxis.MaximumScale = 100;
        chart.YAxis.MajorTickMark = TickMarkType.Outside;
        chart.YAxis.HasMajorGridlines = true;
        chart.XAxis.MajorTickMark = TickMarkType.None;
        chart.TopArea.AddLegend();
        var points = charted.Count == 0 ? 0 : charted[0].Trend.Count;
        var labels = chart.XValues.AddXSeries();
        for (var point = 0; point < points; point++)
        {
            labels.Add(point % Math.Max(1, points / 6) == 0 ? (point + 1).ToString(CultureInfo.InvariantCulture) : "");
        }

        for (var index = 0; index < charted.Count; index++)
        {
            var series = chart.SeriesCollection.AddSeries();
            series.Name = charted[index].Name;
            series.LineFormat.Color = Hex(SeriesColours[index]);
            series.MarkerStyle = MarkerStyle.None;
            foreach (var rate in charted[index].Trend)
            {
                if (rate is { } value)
                {
                    series.Add(value * 100);
                }
                else
                {
                    series.AddBlank();
                }
            }
        }

        section.Add(chart);
        var others = report.Domains.Except(charted).Select(domain => domain.Name).ToList();
        if (others.Count > 0)
        {
            section.AddParagraph($"Not charted: {string.Join(", ", others)}").Format.Font.Size = 8;
        }
    }

    private static void AddDomain(Section section, ClientReportDomain domain)
    {
        section.AddParagraph(domain.Name, StyleNames.Heading2);
        var status = section.AddParagraph(StatusText(domain.Status.Health));
        status.Format.Font.Bold = true;
        status.Format.Font.Color = StatusColour(domain.Status.Health);
        foreach (var reason in domain.Status.Reasons)
        {
            section.AddParagraph(reason);
        }

        section.AddParagraph(domain.PolicySentence).Format.SpaceBefore = Unit.FromPoint(4);

        if (domain.Health.Count > 0)
        {
            var health = NewTable(section, ("Check", 5.0), ("Result", 11.0));
            foreach (var row in domain.Health)
            {
                AddRow(health, row.Name, row.Label);
                health.Rows[^1].Cells[1].Format.Font.Color = HealthColour(row.Colour);
            }
        }

        section.AddParagraph("Who sent as this domain").Format.Font.Bold = true;
        if (domain.TopSenders.Count == 0)
        {
            section.AddParagraph("No mail was reported in this period.");
        }
        else
        {
            var senders = NewTable(section, ("Sender", 5.6), ("Messages", 2.4), ("Passed", 2.4), ("Failed", 2.4), ("Share", 3.2));
            foreach (var sender in domain.TopSenders)
            {
                AddRow(senders, sender.Owner is null ? sender.Ip : $"{sender.Owner} ({sender.Ip})", Count(sender.Messages), Count(sender.Passing), Count(sender.Failing), Percent(sender.Share));
            }
        }

        var receivers = domain.Receivers;
        var total = receivers.Delivered + receivers.Quarantined + receivers.Rejected;
        section.AddParagraph("What receivers did").Format.Font.Bold = true;
        section.AddParagraph(total == 0
            ? "No mail was reported in this period."
            : $"Delivered {Count(receivers.Delivered)} ({Percent((double)receivers.Delivered / total)}), sent to spam {Count(receivers.Quarantined)} ({Percent((double)receivers.Quarantined / total)}), rejected {Count(receivers.Rejected)} ({Percent((double)receivers.Rejected / total)}).");
        if (receivers.FailingDelivered > 0)
        {
            section.AddParagraph($"{Count(receivers.FailingDelivered)} messages failed DMARC but were delivered anyway, because the policy doesn't block them yet.");
        }

        if (domain.Alerts.Count > 0)
        {
            section.AddParagraph("Alerts").Format.Font.Bold = true;
            foreach (var alert in domain.Alerts)
            {
                var raised = ReportPeriod.Format(DateOnly.FromDateTime(alert.CreatedUtc.UtcDateTime));
                var resolved = alert.ResolvedUtc is { } at ? $", resolved {ReportPeriod.Format(DateOnly.FromDateTime(at.UtcDateTime))}" : ", still open";
                section.AddParagraph($"{alert.Title}: raised {raised}{resolved}.");
            }
        }
    }

    private static Table NewTable(Section section, params (string Header, double WidthCm)[] columns)
    {
        var table = section.AddTable();
        table.Borders.Bottom.Width = 0.5;
        table.Borders.Color = Colors.LightGray;
        table.Format.SpaceBefore = Unit.FromPoint(2);
        table.Format.SpaceAfter = Unit.FromPoint(2);
        foreach (var column in columns)
        {
            table.AddColumn(Unit.FromCentimeter(column.WidthCm));
        }

        var header = table.AddRow();
        header.HeadingFormat = true;
        header.Format.Font.Bold = true;
        for (var index = 0; index < columns.Length; index++)
        {
            header.Cells[index].AddParagraph(columns[index].Header);
        }

        return table;
    }

    private static void AddRow(Table table, params string[] values)
    {
        var row = table.AddRow();
        for (var index = 0; index < values.Length; index++)
        {
            row.Cells[index].AddParagraph(values[index]);
        }
    }

    private static string StatusText(PortalHealth health) => health switch
    {
        PortalHealth.Protected => "Protected",
        PortalHealth.MonitoringOnly => "Monitoring only",
        PortalHealth.NoReportsYet => "No reports yet",
        _ => "Needs attention",
    };

    private static Color StatusColour(PortalHealth health) => health switch
    {
        PortalHealth.Protected => Hex("#2E7D32"),
        PortalHealth.NeedsAttention => Hex("#C62828"),
        _ => Hex("#EF6C00"),
    };

    private static Color HealthColour(MudBlazor.Color colour) => colour switch
    {
        MudBlazor.Color.Success => Hex("#2E7D32"),
        MudBlazor.Color.Error => Hex("#C62828"),
        MudBlazor.Color.Warning => Hex("#EF6C00"),
        _ => Colors.Gray,
    };

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Percent(double? value) => value is { } rate ? rate.ToString("P1", CultureInfo.InvariantCulture) : "No mail";

    private static Color Hex(string hex) => new(
        byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    [GeneratedRegex("[^A-Za-z0-9 ._-]")]
    private static partial Regex UnsafePattern();

    [GeneratedRegex(" {2,}")]
    private static partial Regex SpacesPattern();
}
