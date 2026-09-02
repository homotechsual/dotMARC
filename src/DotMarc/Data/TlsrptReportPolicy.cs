namespace DotMarc.Data;

public sealed class TlsrptReportPolicy
{
    public int Id { get; set; }
    public int TlsrptReportId { get; set; }
    public TlsrptReport TlsrptReport { get; set; } = null!;
    public required string PolicyType { get; set; }
    public required string PolicyDomain { get; set; }
    public long SuccessfulSessionCount { get; set; }
    public long FailedSessionCount { get; set; }
    public List<TlsrptFailureDetail> FailureDetails { get; set; } = [];
}