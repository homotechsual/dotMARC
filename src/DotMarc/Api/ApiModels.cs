using DotMarc.Baselines;
using DotMarc.Data;
using DotMarc.Notifications;

namespace DotMarc.Api;

public sealed record ApiNamedRef(int Id, string Name);

public sealed record ApiPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record ApiGroup(int Id, string Name, int DomainCount);

public sealed record ApiTag(int Id, string Name, string Color, int DomainCount);

/// <summary>The DMARC policy a domain should reach, where it comes from (domain, group or system) and whether the domain
/// meets it (meets, below or checking, meaning not yet checked). All lower case.</summary>
public sealed record ApiDmarcTarget(string Policy, string SubdomainPolicy, string Source, string Outcome)
{
    /// <summary>The words the API uses for each outcome, as in the domain list's dmarcTarget filter.</summary>
    public static readonly IReadOnlyList<string> OutcomeNames = ["meets", "below", "checking"];

    /// <summary>Null when no target applies. Needs the domain's Groups loaded (all of them, whatever the key's scope).</summary>
    public static ApiDmarcTarget? For(Domain domain, DnsRecordSettings settings)
    {
        var target = DmarcTargetResolver.Resolve(domain, settings);
        return target is null
            ? null
            : new ApiDmarcTarget(
                target.Policy.ToString().ToLowerInvariant(),
                target.SubdomainPolicy.ToString().ToLowerInvariant(),
                target.Source.ToString().ToLowerInvariant(),
                OutcomeName(DmarcTargetCheck.Outcome(domain, target)));
    }

    private static string OutcomeName(DmarcTargetOutcome outcome) => outcome switch
    {
        DmarcTargetOutcome.Meets => "meets",
        DmarcTargetOutcome.Below => "below",
        _ => "checking",
    };
}

public sealed record ApiDomain(
    int Id, string Name, bool Monitored, IReadOnlyList<ApiNamedRef> Groups, IReadOnlyList<ApiNamedRef> Tags,
    DateTimeOffset? LastReportReceivedUtc, double? PassRate, ApiDmarcTarget? DmarcTarget)
{
    /// <summary>Needs Groups and Tags loaded; the pass rate comes from DomainReadEndpoints.PassRatesAsync.</summary>
    public static ApiDomain From(Domain domain, ApiScope scope, double? passRate, ApiDmarcTarget? dmarcTarget) => new(
        domain.Id,
        domain.Name,
        domain.IsMonitored,
        domain.Groups.Where(group => scope.Includes(group.Id)).OrderBy(group => group.Name).Select(group => new ApiNamedRef(group.Id, group.Name)).ToList(),
        domain.Tags.OrderBy(tag => tag.Name).Select(tag => new ApiNamedRef(tag.Id, tag.Name)).ToList(),
        domain.LastReportReceivedUtc,
        passRate,
        dmarcTarget);
}

public sealed record ApiCheck(string Status, DateTimeOffset? CheckedUtc, string? Detail);

public sealed record ApiDomainHealth(ApiCheck Dmarc, ApiCheck Spf, ApiCheck Dkim, ApiCheck Mx, ApiCheck Tlsrpt, ApiCheck MtaSts, ApiCheck DmarcAuthorization);

public sealed record ApiDmarcPolicy(string? Policy, string? SubdomainPolicy, int? Percent);

public sealed record ApiDnsProvider(string Provider, string? Zone);

public sealed record ApiDomainDetail(
    int Id, string Name, bool Monitored, IReadOnlyList<ApiNamedRef> Groups, IReadOnlyList<ApiNamedRef> Tags,
    DateTimeOffset? LastReportReceivedUtc, double? PassRate, ApiDmarcTarget? DmarcTarget, ApiDomainHealth Health, ApiDmarcPolicy DmarcPolicy, ApiDnsProvider DnsProvider)
{
    public static ApiDomainDetail From(Domain domain, ApiScope scope, double? passRate, ApiDmarcTarget? dmarcTarget)
    {
        var summary = ApiDomain.From(domain, scope, passRate, dmarcTarget);
        return new ApiDomainDetail(
            summary.Id, summary.Name, summary.Monitored, summary.Groups, summary.Tags, summary.LastReportReceivedUtc, summary.PassRate, summary.DmarcTarget,
            new ApiDomainHealth(
                new ApiCheck(domain.DmarcCheckStatus.ToString(), domain.DmarcCheckedUtc, domain.DmarcCheckDetail),
                new ApiCheck(domain.SpfCheckStatus.ToString(), domain.SpfCheckedUtc, domain.SpfCheckDetail),
                new ApiCheck(domain.DkimCheckStatus.ToString(), domain.DkimCheckedUtc, domain.DkimCheckDetail),
                new ApiCheck(domain.MxCheckStatus.ToString(), domain.MxCheckedUtc, domain.MxCheckDetail),
                new ApiCheck(domain.TlsrptCheckStatus.ToString(), domain.TlsrptCheckedUtc, domain.TlsrptCheckDetail),
                new ApiCheck(domain.MtaStsStatus.ToString(), domain.MtaStsCheckedUtc, domain.MtaStsCheckDetail),
                new ApiCheck(domain.DmarcAuthorizationCheckStatus.ToString(), domain.DmarcAuthorizationCheckedUtc, domain.DmarcAuthorizationCheckDetail)),
            new ApiDmarcPolicy(domain.DmarcPolicy?.ToString(), domain.DmarcSubdomainPolicy?.ToString(), domain.DmarcPercent),
            new ApiDnsProvider(domain.DnsProvider.ToString(), domain.DnsZone));
    }
}

public sealed record ApiReasonBreakdown(int BenignOverride, int LocalPolicy, int Other, int NoReasonGiven, int InferredSpfFailure, int InferredDkimFailure, int InferredBothFailure);

public sealed record ApiSource(string SourceIp, int Volume, string Spf, string Dkim, string Disposition);

public sealed record ApiReportSummary(int DomainId, string DomainName, int Days, int TotalVolume, double? PassRate, ApiReasonBreakdown ReasonBreakdown, IReadOnlyList<ApiSource> TopSources);

public sealed record ApiAddDomainRequest(string? Name);

public sealed record ApiSetGroupsRequest(IReadOnlyList<int>? GroupIds);

public sealed record ApiSetTagsRequest(IReadOnlyList<int>? TagIds);

public sealed record ApiSetMonitoringRequest(bool? Monitored);

public sealed record ApiImportDomain(string? Name, IReadOnlyList<string>? Groups, IReadOnlyList<string>? Tags, bool? Monitored);

public sealed record ApiImportRequest(string? ExistingDomains, string? UnknownNames, IReadOnlyList<ApiImportDomain>? Domains);

/// <summary>Outcome is add, update, unchanged, skip, duplicate or invalid in a dry run; after applying, the import's
/// own outcome text.</summary>
public sealed record ApiImportRow(int Line, string Domain, string Outcome, IReadOnlyList<string> Notes);

public sealed record ApiImportUnknownName(string Kind, string Name, string Resolution);

public sealed record ApiImportResponse(
    bool DryRun, int Added, int Updated, int Unchanged, int SkippedExisting, int Invalid, int Duplicates,
    IReadOnlyList<ApiImportRow> Rows, IReadOnlyList<ApiImportUnknownName> UnknownNames, IReadOnlyList<string> Notices);

/// <summary>Subject is what the alert is about: a domain name, or for alerts not about a domain (an expiring API key),
/// a description. Domain is set when the subject is a domain this key can see. Rule is the alert rule that raised it: null when it
/// was raised before rules existed, when the rule has been deleted, and always for a key limited to some groups (a rule's name can
/// name a client the key cannot see).</summary>
public sealed record ApiAlert(
    int Id, string Type, string TypeName, string Subject, ApiNamedRef? Domain, string Severity, string Title, string Message,
    DateTimeOffset RaisedUtc, bool Resolved, DateTimeOffset? ResolvedUtc, bool Acknowledgeable, IReadOnlyList<ApiAlertTicket> Tickets,
    ApiNamedRef? Rule = null);

/// <summary>An alert rule as the API shows it. Times of day are "HH:mm" in the time zone set on the Settings tab of Alerts &amp; PSA. DestinationMode is All or
/// Selected (Destinations is only meaningful for Selected). TicketMode is FollowRules, Never or Always. A destination's URL is never included.</summary>
public sealed record ApiAlertRule(
    int Id, string Kind, string Name, bool Enabled, string Severity, int Position, bool StopProcessing, int? CooldownMinutes, int? AutoCloseDays,
    IReadOnlyDictionary<string, string> Parameters, string DestinationMode, IReadOnlyList<ApiNamedRef> Destinations, string TicketMode,
    ApiQuietHours QuietHours, ApiEscalation Escalation, string AppliesTo, IReadOnlyList<ApiRuleScopeEntry> Scope);

/// <summary>Mode is Off, Hold or Mute. Start and End are null when there is no window.</summary>
public sealed record ApiQuietHours(string Mode, string? Start, string? End, bool IgnoredForCritical);

/// <summary>AfterMinutes is null when the rule does not escalate.</summary>
public sealed record ApiEscalation(int? AfterMinutes, IReadOnlyList<ApiNamedRef> Destinations);

/// <summary>One group, tag or domain a rule applies to (or leaves out, when Exclude is true). Target is Group, Tag or Domain.</summary>
public sealed record ApiRuleScopeEntry(string Target, int Id, bool Exclude);

/// <summary>Kind is Teams, Slack or GenericWebhook. The webhook URL is a secret and is never returned.</summary>
public sealed record ApiAlertDestination(int Id, string Name, string Kind, bool Enabled);

/// <summary>The outcome of acknowledging an alert. TicketClosed is true when no PSA ticket failed to close (also when the
/// alert had none); a ticket that failed stays open in dotMARC and is retried.</summary>
public sealed record ApiAcknowledgement(bool TicketClosed, int TicketsClosed, int TicketsFailed);

public sealed record ApiAuditActor(string Kind, string Name, string? Email);

public sealed record ApiAuditTarget(string Type, string? Id, string? Name);

/// <summary>A changed field; a secret one has Secret true and no values, as stored.</summary>
public sealed record ApiAuditChange(string Field, string? Old, string? New, bool Secret);

public sealed record ApiAuditEntry(
    long Id, DateTimeOffset OccurredUtc, string Kind, ApiAuditActor Actor, string Action, ApiAuditTarget? Target, string Summary,
    IReadOnlyList<ApiAuditChange> Changes);

/// <summary>A sending IP over the range. Passed means SPF or DKIM passed; Spf and Dkim are Pass if they passed in any
/// record; Disposition is the most severe seen. Organization and Country come from dotMARC's cache of IP lookups. Service is
/// the sending service the reports point to (such as Mailchimp), or null when none is recognised. Decision is what the team has
/// decided about the sender: Authorised, UnderReview, Unauthorised, or Unreviewed when nobody has. It comes from the domain's own
/// decision, else its groups', else the baseline, and it is read-only.</summary>
public sealed record ApiSourceDetail(string Ip, int Volume, int Passed, int Failed, double? PassRate, string Spf, string Dkim, string Disposition,
    string? Organization, string? Country, string? Service = null, string? Decision = null);

public sealed record ApiOverrideReason(string Type, string? Comment);

public sealed record ApiAuthDetail(string Mechanism, string Domain, string Result, string? Selector, string? Scope);

public sealed record ApiDmarcRecord(string SourceIp, int Count, string Disposition, string Spf, string Dkim, string HeaderFrom,
    IReadOnlyList<ApiOverrideReason> OverrideReasons, IReadOnlyList<ApiAuthDetail> AuthDetails);

public sealed record ApiDmarcReport(int Id, string ReportingOrg, string ReportId, DateTimeOffset DateRangeBeginUtc, DateTimeOffset DateRangeEndUtc,
    DateTimeOffset ReceivedUtc, int MessageCount, double? PassRate, IReadOnlyList<ApiDmarcRecord> Records)
{
    /// <summary>Needs Records with OverrideReasons and AuthDetails loaded.</summary>
    public static ApiDmarcReport From(Report report)
    {
        var total = report.Records.Sum(record => record.MessageCount);
        var passing = report.Records.Where(DotMarc.Reporting.DomainStatistics.IsPassing).Sum(record => record.MessageCount);
        return new ApiDmarcReport(report.Id, report.ReportingOrg, report.ReportId, report.DateRangeBeginUtc, report.DateRangeEndUtc, report.ReceivedUtc,
            total, total == 0 ? null : (double)passing / total,
            report.Records.Select(record => new ApiDmarcRecord(record.SourceIp, record.MessageCount, record.Disposition.ToString(), record.SpfResult.ToString(),
                record.DkimResult.ToString(), record.HeaderFrom,
                record.OverrideReasons.Select(reason => new ApiOverrideReason(reason.Type.ToString(), reason.Comment)).ToList(),
                record.AuthDetails.Select(detail => new ApiAuthDetail(detail.Mechanism.ToString(), detail.Domain, detail.Result.ToString(), detail.Selector, detail.Scope)).ToList()))
            .ToList());
    }
}

public sealed record ApiTlsFailure(string ResultType, long Sessions, string? ReceivingMx, string? ReasonCode, string? AdditionalInformation);

public sealed record ApiTlsPolicy(string Type, string Domain, long SuccessfulSessions, long FailedSessions, IReadOnlyList<ApiTlsFailure> Failures);

public sealed record ApiTlsReport(int Id, string ReportingOrg, string ReportId, DateTimeOffset DateRangeBeginUtc, DateTimeOffset DateRangeEndUtc,
    DateTimeOffset ReceivedUtc, long SuccessfulSessions, long FailedSessions, IReadOnlyList<ApiTlsPolicy> Policies)
{
    /// <summary>Needs Policies with FailureDetails loaded.</summary>
    public static ApiTlsReport From(TlsrptReport report) => new(
        report.Id, report.ReportingOrg, report.ReportId, report.DateRangeBeginUtc, report.DateRangeEndUtc, report.ReceivedUtc,
        report.Policies.Sum(policy => policy.SuccessfulSessionCount), report.Policies.Sum(policy => policy.FailedSessionCount),
        report.Policies.Select(policy => new ApiTlsPolicy(policy.PolicyType, policy.PolicyDomain, policy.SuccessfulSessionCount, policy.FailedSessionCount,
            policy.FailureDetails.Select(failure => new ApiTlsFailure(failure.ResultType, failure.FailedSessionCount, failure.ReceivingMxHostname,
                failure.FailureReasonCode, failure.AdditionalInformation)).ToList())).ToList());
}

public sealed record ApiStatusCounts(int Protected, int MonitoringOnly, int NeedsAttention, int NoReportsYet);

public sealed record ApiSummary(int DomainCount, double OverallPassRate, int WarningCount, int MissingCount, int SourceCount,
    ApiReasonBreakdown ReasonBreakdown, int? ParseFailureCount, ApiStatusCounts StatusCounts);

public sealed record ApiReportPeriod(DateOnly Start, DateOnly End, string Kind);

public sealed record ApiReportSchedule(string Frequency, IReadOnlyList<string> Recipients, DateTimeOffset? StartedUtc, ApiReportPeriod? NextPeriod, DateTimeOffset? NextDueUtc);

public sealed record ApiReportDelivery(int Id, DateOnly PeriodStart, DateOnly PeriodEnd, string Kind, string Status, IReadOnlyList<string> Recipients,
    int Attempts, DateTimeOffset? FirstAttemptUtc, DateTimeOffset? LastAttemptUtc, DateTimeOffset? SentUtc, string? Error, string? RequestedBy)
{
    public static ApiReportDelivery From(DotMarc.Reporting.ClientReports.ClientReportDelivery delivery) => new(
        delivery.Id, delivery.PeriodStart, delivery.PeriodEnd, delivery.Kind.ToString(), delivery.Status.ToString(), delivery.Recipients,
        delivery.Attempts, delivery.FirstAttemptUtc, delivery.LastAttemptUtc, delivery.SentUtc, delivery.Error, delivery.RequestedBy);
}

/// <summary>Every field is optional: give period (with of, or for the period in progress) or start and end; recipients
/// defaults to the group's schedule.</summary>
public sealed record ApiSendReportRequest(string? Period = null, string? Of = null, string? Start = null, string? End = null, IReadOnlyList<string>? Recipients = null);

public sealed record ApiParseFailure(int Id, string MessageId, string Reason, int Attempts, DateTimeOffset LastAttemptUtc);

public sealed record ApiAlertTicket(string Psa, string TicketId, bool Open, DateTimeOffset CreatedUtc, DateTimeOffset? LastCheckedUtc);
