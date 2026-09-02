namespace DotMarc.Ingestion;

public sealed record ParsedTlsrptReport(string Domain, string ReportingOrg, string ReportId, DateTimeOffset DateRangeBeginUtc, DateTimeOffset DateRangeEndUtc, IReadOnlyList<ParsedTlsrptPolicy> Policies);
public sealed record ParsedTlsrptPolicy(string PolicyType, string PolicyDomain, long SuccessfulSessionCount, long FailedSessionCount, IReadOnlyList<ParsedTlsrptFailureDetail> FailureDetails);
public sealed record ParsedTlsrptFailureDetail(string ResultType, long FailedSessionCount, string? ReceivingMxHostname, string? FailureReasonCode, string? AdditionalInformation);