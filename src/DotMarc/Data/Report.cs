namespace DotMarc.Data;

/// <summary>One aggregate report as received from one reporting organization, covering one date
/// range. <see cref="RawXml"/> is kept for the 12-month raw-retention window described in the
/// design spec.</summary>
public sealed class Report
{
    public int Id { get; set; }
    public int DomainId { get; set; }
    public Domain Domain { get; set; } = null!;
    public required string ReportingOrg { get; set; }
    public required string ReportId { get; set; }
    public DateTimeOffset DateRangeBeginUtc { get; set; }
    public DateTimeOffset DateRangeEndUtc { get; set; }
    public required string RawXml { get; set; }
    public DateTimeOffset ReceivedUtc { get; set; }

    public List<ReportRecord> Records { get; set; } = [];
}
