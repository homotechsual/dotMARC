using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Demo;

/// <summary>Wipes and rewrites every app-owned table from a DemoDataset — the same code path
/// runs on first boot and on every scheduled reset (see DemoDataResetService), so there is only
/// one seeding path, not two. Deliberately does not use AccessBootstrapper's advisory-lock
/// pattern: this always runs against a single demo instance from either Program.cs's startup
/// block or DemoDataResetService's own serial loop, never concurrently with itself.</summary>
public static class DemoDataSeeder
{
    public const string AdminEmail = "demo-admin@nova-msp.example";
    public const string ViewerEmail = "demo-viewer@nova-msp.example";
    public const string ViewerScopedGroupName = "Aurora Retail";

    public static async Task ResetAsync(DotMarcDbContext context, DemoDataset dataset, CancellationToken cancellationToken = default)
    {
        // Wrapped in a transaction per the design spec's "writes it... inside a transaction"
        // requirement: Postgres's TRUNCATE ... RESTART IDENTITY is fully transactional, so a
        // mid-reset failure (e.g. WriteAsync throwing partway through) rolls the truncate back
        // too, instead of leaving the database truncated with zero UserAccess rows — which would
        // otherwise deny every visitor access until the next scheduled reset, up to 24h later.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await TruncateAllTablesAsync(context, cancellationToken).ConfigureAwait(false);
        await WriteAsync(context, dataset, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // "IpInfos" and "IpRanges" are deliberately NOT in this list: they're a shared
    // external-lookup cache (RDAP ownership/country data, keyed by IP address or by allocation
    // block, not by domain/report), not demo-narrative data — wiping them on every reset would
    // just force every demo IP to be re-looked-up against rdap.org for no benefit.
    internal static Task TruncateAllTablesAsync(DotMarcDbContext context, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE
                "Domains", "Reports", "ReportRecords", "Groups", "Tags", "Roles", "UserAccesses",
                "PollCycles", "PollCycleDailySummaries", "ParseFailures", "ProcessedMessages",
                "UserAccessScopedGroups", "DomainGroup", "DomainTag"
            RESTART IDENTITY CASCADE
            """,
            cancellationToken);

    internal static async Task WriteAsync(DotMarcDbContext context, DemoDataset dataset, CancellationToken cancellationToken)
    {
        var adminRole = new Role { Name = "Admin", IsLocked = true, IsScopable = false, Permissions = [.. Enum.GetValues<Permission>()] };
        var viewerRole = new Role { Name = "Viewer", IsLocked = false, IsScopable = true, Permissions = AccessBootstrapper.ViewerPermissions };
        context.Roles.AddRange(adminRole, viewerRole);

        var groupsByName = dataset.Groups.ToDictionary(g => g.Name, g => new Group { Name = g.Name });
        context.Groups.AddRange(groupsByName.Values);

        foreach (var domainSeed in dataset.Domains)
        {
            var domain = new Domain
            {
                Name = domainSeed.Name,
                IsMonitored = true,
                SortOrder = domainSeed.SortOrder,
                FirstSeenUtc = domainSeed.FirstSeenUtc,
                LastReportReceivedUtc = domainSeed.LastReportReceivedUtc,
                DmarcCheckStatus = domainSeed.DmarcCheckStatus,
                DmarcCheckedUtc = domainSeed.DmarcCheckStatus == DmarcCheckStatus.NotChecked ? null : domainSeed.FirstSeenUtc,
                DmarcCheckDetail = domainSeed.DmarcCheckDetail
            };

            if (domainSeed.GroupName is not null)
            {
                domain.Groups.Add(groupsByName[domainSeed.GroupName]);
            }

            foreach (var reportSeed in domainSeed.Reports)
            {
                var report = new Report
                {
                    Domain = domain,
                    ReportingOrg = reportSeed.ReportingOrg,
                    ReportId = reportSeed.ReportId,
                    DateRangeBeginUtc = reportSeed.DateRangeBeginUtc,
                    DateRangeEndUtc = reportSeed.DateRangeEndUtc,
                    RawXml = "<!-- demo data: no raw report retained -->",
                    ReceivedUtc = reportSeed.DateRangeEndUtc
                };

                foreach (var recordSeed in reportSeed.Records)
                {
                    report.Records.Add(new ReportRecord
                    {
                        Report = report,
                        SourceIp = recordSeed.SourceIp,
                        MessageCount = recordSeed.MessageCount,
                        Disposition = recordSeed.Disposition,
                        SpfResult = recordSeed.SpfResult,
                        DkimResult = recordSeed.DkimResult,
                        HeaderFrom = domainSeed.Name
                    });
                }

                domain.Reports.Add(report);
            }

            context.Domains.Add(domain);
        }

        foreach (var pollCycle in dataset.PollCycles)
        {
            context.PollCycles.Add(new PollCycle
            {
                PolledUtc = pollCycle.PolledUtc,
                MessagesChecked = pollCycle.MessagesChecked,
                ReportsParsed = pollCycle.ReportsParsed,
                ParseFailures = pollCycle.ParseFailures,
                Succeeded = pollCycle.Succeeded,
                ErrorMessage = pollCycle.ErrorMessage
            });
        }

        foreach (var summary in dataset.PollCycleDailySummaries)
        {
            context.PollCycleDailySummaries.Add(new PollCycleDailySummary
            {
                Date = summary.Date,
                TotalCycles = summary.TotalCycles,
                SuccessfulCycles = summary.SuccessfulCycles,
                FailedCycles = summary.FailedCycles,
                TotalMessagesChecked = summary.TotalMessagesChecked,
                TotalReportsParsed = summary.TotalReportsParsed,
                TotalParseFailures = summary.TotalParseFailures
            });
        }

        foreach (var failure in dataset.ParseFailures)
        {
            context.ParseFailures.Add(new ParseFailure
            {
                GraphMessageId = failure.GraphMessageId,
                Reason = failure.Reason,
                AttemptCount = failure.AttemptCount,
                LastAttemptedUtc = failure.LastAttemptedUtc
            });
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        context.UserAccesses.AddRange(
            new UserAccess { Email = AdminEmail, Role = adminRole },
            new UserAccess { Email = ViewerEmail, Role = viewerRole, ScopedGroups = [groupsByName[ViewerScopedGroupName]] });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
