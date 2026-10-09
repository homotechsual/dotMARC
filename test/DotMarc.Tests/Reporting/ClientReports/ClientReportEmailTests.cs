using DotMarc.Reporting.ClientReports;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

public sealed class ClientReportEmailTests
{
    [Fact]
    public void TheEmail_HasTheSubjectSummaryAndAttachment()
    {
        var report = ClientReportDocumentTests.SampleReport();

        var message = ClientReportEmail.Compose(report, [0x25, 0x50], ["it@aurora-retail.example"]);

        Assert.Equal("Aurora Retail Ltd email security report: March 2026", message.Subject);
        Assert.Contains("1 of 2 domains are fully protected.", message.HtmlBody);
        Assert.Contains("aurora-retail.example", message.HtmlBody);
        Assert.Contains("The full report is attached.", message.TextBody);
        Assert.Equal(("Aurora Retail Ltd email security report March 2026.pdf", "application/pdf"),
            (message.Attachments.Single().FileName, message.Attachments.Single().ContentType));
        Assert.DoesNotContain("<img", message.HtmlBody);
    }

    [Fact]
    public void Numbers_FollowTheReportsNumberFormat_NotTheServersCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
        try
        {
            var british = ClientReportEmail.Compose(ClientReportDocumentTests.SampleReport() with { NumberFormat = "en-GB" }, [0x25], ["it@aurora-retail.example"]);
            var german = ClientReportEmail.Compose(ClientReportDocumentTests.SampleReport() with { NumberFormat = "de-DE" }, [0x25], ["it@aurora-retail.example"]);

            Assert.Contains("1,200", british.HtmlBody);
            Assert.Contains("1,200 messages", british.TextBody);
            Assert.Contains("1.200", german.HtmlBody);
            Assert.Contains("95,0 %", german.HtmlBody);
            Assert.Contains("1.200 messages", german.TextBody);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void NamesFromData_AreText_NotMarkup()
    {
        var report = ClientReportDocumentTests.SampleReport() with
        {
            Brand = ClientReportDocumentTests.SampleReport().Brand with { Heading = "<script>alert(1)</script>" },
        };

        var message = ClientReportEmail.Compose(report, [0x25], ["it@aurora-retail.example"]);

        Assert.DoesNotContain("<script>", message.HtmlBody);
        Assert.Contains("&lt;script&gt;", message.HtmlBody);
    }
}
