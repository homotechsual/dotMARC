using DotMarc.Data;

namespace DotMarc.Demo;

/// <summary>Everything DemoDataSeeder needs to (re)populate the database for one reset cycle.
/// Produced by the pure DemoDataGenerator — see that class for the narrative this data tells.</summary>
public sealed record DemoDataset(
    List<DemoGroupSeed> Groups,
    List<DemoDomainSeed> Domains,
    List<DemoPollCycleSeed> PollCycles,
    List<DemoPollCycleDailySummarySeed> PollCycleDailySummaries,
    List<DemoParseFailureSeed> ParseFailures);

public sealed record DemoGroupSeed(string Name);

public sealed record DemoDomainSeed(
    string Name,
    string? GroupName,
    int SortOrder,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset? LastReportReceivedUtc,
    DmarcCheckStatus DmarcCheckStatus,
    string? DmarcCheckDetail,
    List<DemoReportSeed> Reports);

public sealed record DemoReportSeed(
    string ReportingOrg,
    string ReportId,
    DateTimeOffset DateRangeBeginUtc,
    DateTimeOffset DateRangeEndUtc,
    List<DemoRecordSeed> Records);

public sealed record DemoRecordSeed(
    string SourceIp,
    int MessageCount,
    AuthResult SpfResult,
    AuthResult DkimResult,
    DispositionResult Disposition);

public sealed record DemoPollCycleSeed(
    DateTimeOffset PolledUtc,
    int MessagesChecked,
    int ReportsParsed,
    int ParseFailures,
    bool Succeeded,
    string? ErrorMessage);

public sealed record DemoPollCycleDailySummarySeed(
    DateOnly Date,
    int TotalCycles,
    int SuccessfulCycles,
    int FailedCycles,
    int TotalMessagesChecked,
    int TotalReportsParsed,
    int TotalParseFailures);

public sealed record DemoParseFailureSeed(
    string GraphMessageId,
    string Reason,
    int AttemptCount,
    DateTimeOffset LastAttemptedUtc);
