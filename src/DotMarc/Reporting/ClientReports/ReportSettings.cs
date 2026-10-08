namespace DotMarc.Reporting.ClientReports;

/// <summary>Singleton row (seeded <c>Id = 1</c>): the time zone report periods are measured in, and the local hour
/// scheduled reports go out.</summary>
public sealed class ReportSettings
{
    public int Id { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public int SendHour { get; set; } = 6;
}
