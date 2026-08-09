using DmarcRua;

namespace DotMarc.Ingestion;

/// <summary>Wraps DmarcRua's AggregateReport parser. DmarcRua itself only throws for input it
/// cannot deserialize as XML at all (e.g. garbage bytes) — well-formed XML that fails schema
/// validation instead sets ValidReport = false without throwing (confirmed empirically against
/// DmarcRua 2.0.1). This wrapper treats both cases identically as failures, since PollingService's
/// failure handling (Task 6) needs a single exception type to catch.</summary>
public static class DmarcReportParser
{
    public static ParsedReport Parse(byte[] xmlBytes)
    {
        AggregateReport report;
        try
        {
            using var stream = new MemoryStream(xmlBytes);
            report = new AggregateReport(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException("Could not deserialize DMARC aggregate report XML.", ex);
        }

        if (!report.ValidReport || report.Feedback is null)
        {
            throw new InvalidDataException("DMARC aggregate report failed schema validation.");
        }

        var feedback = report.Feedback;
        var records = feedback.Record.Select(r => new ParsedReportRecord(
            r.Row.SourceIp,
            r.Row.Count,
            r.Row.PolicyEvaluated.Disposition.ToString(),
            r.Row.PolicyEvaluated.Spf.ToString(),
            r.Row.PolicyEvaluated.Dkim.ToString(),
            r.Identifiers.HeaderFrom)).ToList();

        return new ParsedReport(
            feedback.PolicyPublished.Domain,
            feedback.ReportMetadata.OrgName,
            feedback.ReportMetadata.ReportId,
            DateTimeOffset.FromUnixTimeSeconds(feedback.ReportMetadata.DateRange.Begin),
            DateTimeOffset.FromUnixTimeSeconds(feedback.ReportMetadata.DateRange.End),
            records);
    }
}
