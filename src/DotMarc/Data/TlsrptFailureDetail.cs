namespace DotMarc.Data;

public sealed class TlsrptFailureDetail
{
    public int Id { get; set; }
    public int TlsrptReportPolicyId { get; set; }
    public TlsrptReportPolicy TlsrptReportPolicy { get; set; } = null!;
    public required string ResultType { get; set; }
    public long FailedSessionCount { get; set; }
    public string? ReceivingMxHostname { get; set; }
    public string? FailureReasonCode { get; set; }
    public string? AdditionalInformation { get; set; }
}