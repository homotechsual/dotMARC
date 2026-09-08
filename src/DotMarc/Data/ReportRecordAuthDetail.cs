namespace DotMarc.Data;

/// <summary>One DKIM signature or SPF check found in a report record's &lt;auth_results&gt; -
/// a record commonly has one of each, but DKIM can carry several signatures. Mirrors
/// TlsrptFailureDetail's one-parent-many-children shape.</summary>
public sealed class ReportRecordAuthDetail
{
    public int Id { get; set; }
    public int ReportRecordId { get; set; }
    public ReportRecord ReportRecord { get; set; } = null!;
    public required DmarcAuthMechanism Mechanism { get; set; }
    public required string Domain { get; set; }
    public required DmarcMechanismResult Result { get; set; }
    public string? Selector { get; set; }   // DKIM only
    public string? Scope { get; set; }      // SPF only: "MFrom" or "Helo"
    public string? HumanResult { get; set; }
}
