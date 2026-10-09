namespace DotMarc.Reporting.ClientReports;

/// <summary>A Group's report schedule. <see cref="StartedUtc"/> is when the current frequency was set: periods that
/// fell due before it aren't sent, so turning a schedule on never sends a backlog.</summary>
public sealed class GroupReportSchedule
{
    public int GroupId { get; set; }
    public ReportFrequency Frequency { get; set; }
    public List<string> Recipients { get; set; } = [];
    public DateTimeOffset StartedUtc { get; set; }
}
