namespace DotMarc.Data;

public sealed class TlsrptReport
{
    public int Id { get; set; }
    public int DomainId { get; set; }
    public Domain Domain { get; set; } = null!;
    public required string ReportingOrg { get; set; }
    public required string ReportId { get; set; }
    public DateTimeOffset DateRangeBeginUtc { get; set; }
    public DateTimeOffset DateRangeEndUtc { get; set; }
    public required string RawJson { get; set; }
    public DateTimeOffset ReceivedUtc { get; set; }
    public List<TlsrptReportPolicy> Policies { get; set; } = [];
}