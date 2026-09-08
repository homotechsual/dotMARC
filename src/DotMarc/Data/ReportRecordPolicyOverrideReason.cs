namespace DotMarc.Data;

/// <summary>One &lt;reason&gt; entry from a report record's policy_evaluated block - usually 0 or
/// 1 per record, but the DMARC schema allows several.</summary>
public sealed class ReportRecordPolicyOverrideReason
{
    public int Id { get; set; }
    public int ReportRecordId { get; set; }
    public ReportRecord ReportRecord { get; set; } = null!;
    public required DmarcPolicyOverrideType Type { get; set; }
    public string? Comment { get; set; }
}
