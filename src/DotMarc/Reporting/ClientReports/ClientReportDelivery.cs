namespace DotMarc.Reporting.ClientReports;

public enum ClientReportDeliveryKind { Scheduled, Manual }

public enum ClientReportDeliveryStatus { Pending, Sent, Failed, Skipped }

/// <summary>One report sent, or tried, for a Group and period. A scheduled period has at most one row (a unique index),
/// which is how it's sent only once; manual sends get a row each and never affect the schedule.</summary>
public sealed class ClientReportDelivery
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public ClientReportDeliveryKind Kind { get; set; }
    public List<string> Recipients { get; set; } = [];

    /// <summary>Who sent a manual report.</summary>
    public string? RequestedBy { get; set; }

    public ClientReportDeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? FirstAttemptUtc { get; set; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public DateTimeOffset? SentUtc { get; set; }

    /// <summary>The last failure, or why the period was skipped.</summary>
    public string? Error { get; set; }
}
