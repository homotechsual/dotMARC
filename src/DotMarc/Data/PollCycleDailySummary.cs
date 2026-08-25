namespace DotMarc.Data;

/// <summary>One row per UTC calendar day, created/updated only once that day's raw PollCycle rows
/// are more than 7 days old and get rolled up (see PollingService.RollUpStalePollCyclesAsync). Kept
/// indefinitely — small compared to the raw rows it replaces (one row per day instead of one row
/// per poll cycle).</summary>
public sealed class PollCycleDailySummary
{
    public int Id { get; set; }
    public required DateOnly Date { get; set; }
    public int TotalCycles { get; set; }
    public int SuccessfulCycles { get; set; }
    public int FailedCycles { get; set; }
    public int TotalMessagesChecked { get; set; }
    public int TotalReportsParsed { get; set; }
    public int TotalParseFailures { get; set; }
}
