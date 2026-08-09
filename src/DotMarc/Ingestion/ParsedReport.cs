namespace DotMarc.Ingestion;

public sealed record ParsedReport(
    string Domain,
    string ReportingOrg,
    string ReportId,
    DateTimeOffset DateRangeBeginUtc,
    DateTimeOffset DateRangeEndUtc,
    IReadOnlyList<ParsedReportRecord> Records);

public sealed record ParsedReportRecord(
    string SourceIp,
    int MessageCount,
    string Disposition,
    string SpfResult,
    string DkimResult,
    string HeaderFrom);
