namespace DotMarc.Data;

/// <summary>One row per poll cycle that actually ran (a cycle skipped because another replica held
/// the leader lock — see PollingService.RunPollCycleAsync — writes nothing here; "last polled"
/// should reflect when polling actually happened, not a skip). Raw rows are kept for 7 days, then
/// rolled up into PollCycleDailySummary and deleted (see PollingService.RollUpStalePollCyclesAsync).</summary>
public sealed class PollCycle
{
    public int Id { get; set; }
    public DateTimeOffset PolledUtc { get; set; }
    public int MessagesChecked { get; set; }
    public int ReportsParsed { get; set; }
    public int ParseFailures { get; set; }
    public bool Succeeded { get; set; }
    public string? ErrorMessage { get; set; }
}
