using System.Net;
using System.Text;
using DotMarc.Email;

namespace DotMarc.Reporting.ClientReports;

/// <summary>The short branded email a report goes out in: the verdict and summary, with the PDF attached. No remote
/// images, so nothing is blocked or tracked. Everything from data is HTML-encoded.</summary>
public static class ClientReportEmail
{
    public static string Subject(ClientReport report) => $"{report.Brand.Heading} email security report: {report.Period.Label}";

    public static EmailMessage Compose(ClientReport report, byte[] pdf, IReadOnlyList<string> to)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        var culture = report.Culture;

        var html = new StringBuilder();
        html.Append("<div style=\"font-family:Arial,Helvetica,sans-serif;font-size:14px;color:#222\">");
        html.Append($"<div style=\"border-top:6px solid {E(report.Brand.PrimaryColour)};padding-top:12px\">");
        html.Append($"<h1 style=\"font-size:20px;margin:0 0 4px\">{E(report.Brand.Heading)}</h1>");
        html.Append($"<p style=\"margin:0 0 12px;color:#555\">Email security report: {E(report.Period.Label)}</p>");
        html.Append($"<p style=\"font-weight:bold\">{E(report.Verdict)}</p>");
        if (report.Domains.Count > 0)
        {
            html.Append("<table cellpadding=\"6\" style=\"border-collapse:collapse\"><tr style=\"text-align:left\"><th>Domain</th><th>Messages</th><th>Pass rate</th><th>Change</th></tr>");
            foreach (var domain in report.Domains)
            {
                html.Append($"<tr><td>{E(domain.Name)}</td><td>{E(domain.Messages.ToString("N0", culture))}</td><td>{E(domain.PassRate?.ToString("P1", culture) ?? "No mail")}</td><td>{E(domain.FormatChange(culture))}</td></tr>");
            }

            html.Append("</table>");
        }

        html.Append("<p>The full report is attached.</p>");
        var contact = new[] { report.Brand.SupportEmail, report.Brand.SupportUrl, report.Brand.SupportPhone }.OfType<string>().ToList();
        if (contact.Count > 0)
        {
            html.Append($"<p style=\"color:#555\">Questions? {E(string.Join("  |  ", contact))}</p>");
        }

        html.Append($"<p style=\"color:#888;font-size:12px\">{E(report.Brand.ProductName)}</p></div></div>");

        var text = new StringBuilder()
            .AppendLine($"{report.Brand.Heading}: email security report, {report.Period.Label}")
            .AppendLine()
            .AppendLine(report.Verdict)
            .AppendLine();
        foreach (var domain in report.Domains)
        {
            text.AppendLine($"{domain.Name}: {domain.Messages.ToString("N0", culture)} messages, pass rate {domain.PassRate?.ToString("P1", culture) ?? "no mail"} {domain.FormatChange(culture)}".TrimEnd());
        }

        text.AppendLine().AppendLine("The full report is attached.");
        if (contact.Count > 0)
        {
            text.AppendLine($"Questions? {string.Join("  |  ", contact)}");
        }

        return new EmailMessage(to, Subject(report), html.ToString(), text.ToString(),
            [new EmailAttachment(ClientReportDocument.FileName(report), "application/pdf", pdf)]);
    }
}
