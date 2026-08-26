namespace DotMarc.Data;

/// <summary>A monitored domain. Rows are created automatically the first time a report arrives
/// for a domain (auto-discovery); <see cref="IsMonitored"/> is set explicitly via the dashboard
/// and only affects whether a missing-report warning is shown for that domain — it does not
/// change ingestion behavior.</summary>
public sealed class Domain
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool IsMonitored { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset? LastReportReceivedUtc { get; set; }

    public List<Report> Reports { get; set; } = [];
}
