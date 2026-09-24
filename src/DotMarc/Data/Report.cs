using System.ComponentModel.DataAnnotations.Schema;

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
    public DateTimeOffset ReceivedUtc { get; set; }
    public DateTimeOffset? AuthDetailBackfilledUtc { get; set; }

    public List<ReportRecord> Records { get; set; } = [];

    /// <summary>The raw XML, held in a separate entity so ordinary report queries don't read it.
    /// Null unless the query used <c>Include(r =&gt; r.Raw)</c>.</summary>
    public ReportRawXml? Raw { get; set; }

    /// <summary>Reads or sets the raw XML. Reading it when the query didn't include
    /// <see cref="Raw"/> throws rather than returning an empty string: an empty value would look
    /// like a report with no XML (and the auth-detail backfill would permanently mark it handled),
    /// which is much worse than a loud failure.</summary>
    [NotMapped]
    public required string RawXml
    {
        get => Raw?.Xml ?? throw new InvalidOperationException("Report.RawXml wasn't loaded. Query with .Include(r => r.Raw) when the raw XML is needed.");
        set
        {
            if (Raw is null)
            {
                Raw = new ReportRawXml { Xml = value };
            }
            else
            {
                Raw.Xml = value;
            }
        }
    }
}
