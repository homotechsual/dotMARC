namespace DotMarc.Reporting.ClientReports;

/// <summary>Singleton row (seeded <c>Id = 1</c>): the time zone report periods are measured in, and the local hour
/// scheduled reports go out.</summary>
public sealed class ReportSettings
{
    public int Id { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public int SendHour { get; set; } = 6;

    /// <summary>The region whose number format reports use (separators and percent style), such as en-GB or de-DE.
    /// The report's words, and its dates, stay in English.</summary>
    public string NumberFormat { get; set; } = DefaultNumberFormat;

    public const string DefaultNumberFormat = "en-GB";
}
