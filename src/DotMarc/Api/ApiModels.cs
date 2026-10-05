using DotMarc.Data;
using DotMarc.Reporting;

namespace DotMarc.Api;

public sealed record ApiNamedRef(int Id, string Name);

public sealed record ApiPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record ApiGroup(int Id, string Name, int DomainCount);

public sealed record ApiTag(int Id, string Name, string Color, int DomainCount);

public sealed record ApiDomain(
    int Id, string Name, bool Monitored, IReadOnlyList<ApiNamedRef> Groups, IReadOnlyList<ApiNamedRef> Tags,
    DateTimeOffset? LastReportReceivedUtc, double? PassRate)
{
    /// <summary>Needs Groups, Tags and the 30-day window's Reports with their Records loaded.</summary>
    public static ApiDomain From(Domain domain, ApiScope scope) => new(
        domain.Id,
        domain.Name,
        domain.IsMonitored,
        domain.Groups.Where(group => scope.Includes(group.Id)).OrderBy(group => group.Name).Select(group => new ApiNamedRef(group.Id, group.Name)).ToList(),
        domain.Tags.OrderBy(tag => tag.Name).Select(tag => new ApiNamedRef(tag.Id, tag.Name)).ToList(),
        domain.LastReportReceivedUtc,
        DomainStatistics.GetPassRate(domain.Reports));
}

public sealed record ApiCheck(string Status, DateTimeOffset? CheckedUtc, string? Detail);

public sealed record ApiDomainHealth(ApiCheck Dmarc, ApiCheck Spf, ApiCheck Dkim, ApiCheck Mx, ApiCheck Tlsrpt, ApiCheck MtaSts, ApiCheck DmarcAuthorization);

public sealed record ApiDmarcPolicy(string? Policy, string? SubdomainPolicy, int? Percent);

public sealed record ApiDnsProvider(string Provider, string? Zone);

public sealed record ApiDomainDetail(
    int Id, string Name, bool Monitored, IReadOnlyList<ApiNamedRef> Groups, IReadOnlyList<ApiNamedRef> Tags,
    DateTimeOffset? LastReportReceivedUtc, double? PassRate, ApiDomainHealth Health, ApiDmarcPolicy DmarcPolicy, ApiDnsProvider DnsProvider)
{
    public static ApiDomainDetail From(Domain domain, ApiScope scope)
    {
        var summary = ApiDomain.From(domain, scope);
        return new ApiDomainDetail(
            summary.Id, summary.Name, summary.Monitored, summary.Groups, summary.Tags, summary.LastReportReceivedUtc, summary.PassRate,
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
/// a description. Domain is set when the subject is a domain this key can see.</summary>
public sealed record ApiAlert(
    int Id, string Type, string TypeName, string Subject, ApiNamedRef? Domain, string Severity, string Title, string Message,
    DateTimeOffset RaisedUtc, bool Resolved, DateTimeOffset? ResolvedUtc, bool Acknowledgeable);

public sealed record ApiAcknowledgement(bool TicketClosed);
