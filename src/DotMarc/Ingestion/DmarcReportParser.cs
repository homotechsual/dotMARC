using DmarcRua;
using DotMarc.Data;

namespace DotMarc.Ingestion;

/// <summary>Wraps DmarcRua's AggregateReport parser. DmarcRua itself only throws for input it
/// cannot deserialize as XML at all (e.g. garbage bytes) - well-formed XML that fails schema
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
            r.Identifiers.HeaderFrom,
            MapAuthDetails(r.AuthResults),
            MapOverrideReasons(r.Row.PolicyEvaluated.Reason))).ToList();

        return new ParsedReport(
            feedback.PolicyPublished.Domain,
            feedback.ReportMetadata.OrgName,
            feedback.ReportMetadata.ReportId,
            DateTimeOffset.FromUnixTimeSeconds(feedback.ReportMetadata.DateRange.Begin),
            DateTimeOffset.FromUnixTimeSeconds(feedback.ReportMetadata.DateRange.End),
            records);
    }

    private static List<ParsedAuthDetail> MapAuthDetails(AuthResultType authResults)
    {
        var details = new List<ParsedAuthDetail>();

        foreach (var dkim in authResults.Dkim ?? [])
        {
            details.Add(new ParsedAuthDetail(
                DmarcAuthMechanism.Dkim,
                dkim.Domain,
                Enum.Parse<DmarcMechanismResult>(dkim.Result.ToString()),
                dkim.Selector,
                null,
                dkim.HumanResult));
        }

        foreach (var spf in authResults.Spf ?? [])
        {
            details.Add(new ParsedAuthDetail(
                DmarcAuthMechanism.Spf,
                spf.Domain,
                Enum.Parse<DmarcMechanismResult>(spf.Result.ToString()),
                null,
                spf.Scope?.ToString(),
                spf.HumanResult));
        }

        return details;
    }

    private static List<ParsedPolicyOverrideReason> MapOverrideReasons(PolicyOverrideReason[]? reasons) =>
        (reasons ?? []).Select(r => new ParsedPolicyOverrideReason(Enum.Parse<DmarcPolicyOverrideType>(r.Type.ToString()), r.Comment)).ToList();
}
