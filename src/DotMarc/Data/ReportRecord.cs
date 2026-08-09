namespace DotMarc.Data;

/// <summary>One sending source within a report: what a domain's Sources tab and trend charts
/// query against. A single Report typically has several of these, one per source IP.</summary>
public sealed class ReportRecord
{
    public int Id { get; set; }
    public int ReportId { get; set; }
    public Report Report { get; set; } = null!;
    public required string SourceIp { get; set; }
    public int MessageCount { get; set; }
    public DispositionResult Disposition { get; set; }
    public AuthResult SpfResult { get; set; }
    public AuthResult DkimResult { get; set; }
    public required string HeaderFrom { get; set; }
}
