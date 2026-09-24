namespace DotMarc.Data;

/// <summary>The raw XML of one <see cref="Report"/>. It lives in the same table row as the report
/// (EF table splitting, so there is no schema difference) but is mapped as its own entity so it is
/// only read when a query asks for it with <c>Include(r =&gt; r.Raw)</c>. It is by far the widest
/// column, and the dashboard, domain pages and alerting never look at it, so loading it with every
/// report would only move megabytes of XML for nothing.</summary>
public sealed class ReportRawXml
{
    /// <summary>Shared with <see cref="Report.Id"/>: this is the same row.</summary>
    public int Id { get; set; }

    public required string Xml { get; set; }
}
