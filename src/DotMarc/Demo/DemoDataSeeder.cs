using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Demo;

/// <summary>Wipes and rewrites every app-owned table from a DemoDataset - the same code path
/// runs on first boot and on every scheduled reset (see DemoDataResetService), so there is only
/// one seeding path, not two. Deliberately does not use AccessBootstrapper's advisory-lock
/// pattern: this always runs against a single demo instance from either Program.cs's startup
/// block or DemoDataResetService's own serial loop, never concurrently with itself.</summary>
public static class DemoDataSeeder
{
    public const string AdminEmail = "demo-admin@nova-msp.example";
    public const string ViewerEmail = "demo-viewer@nova-msp.example";
    public const string ViewerScopedGroupName = "Aurora Retail";
    public const string ClientEmail = "demo-client@aurora-retail.example";

    public static async Task ResetAsync(DotMarcDbContext context, DemoDataset dataset, CancellationToken cancellationToken = default)
    {
        // Wrapped in a transaction per the design spec's "writes it... inside a transaction"
        // requirement: Postgres's TRUNCATE ... RESTART IDENTITY is fully transactional, so a
        // mid-reset failure (e.g. WriteAsync throwing partway through) rolls the truncate back
        // too, instead of leaving the database truncated with zero UserAccess rows - which would
        // otherwise deny every visitor access until the next scheduled reset, up to 24h later.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await TruncateAllTablesAsync(context, cancellationToken).ConfigureAwait(false);
        await WriteAsync(context, dataset, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // "IpInfos" and "IpRanges" are deliberately NOT in this list: they're a shared
    // external-lookup cache (RDAP ownership/country data, keyed by IP address or by allocation
    // block, not by domain/report), not demo-narrative data - wiping them on every reset would
    // just force every demo IP to be re-looked-up against rdap.org for no benefit.
    //
    // "NotificationSettings" is also deliberately NOT in this list, for the same "shared
    // config, not demo-narrative data" reason - plus it's a migration-seeded singleton row (see
    // NotificationSettings's doc comment), so truncating it would leave the table empty until a
    // fresh row was reseeded, breaking every reader's SingleAsync assumption. "AlertEvents" IS
    // included: unlike those two, an alert is inherently tied to one demo domain's history, and
    // that history is rewritten by every reset. "TlsrptReports"/"TlsrptReportPolicies"/
    // "TlsrptFailureDetails" are listed explicitly even though CASCADE would already catch them
    // via their FK chain back to Domains, matching how every other domain-owned table here is
    // named rather than relied on implicitly.
    internal static Task TruncateAllTablesAsync(DotMarcDbContext context, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE
                "Domains", "Reports", "ReportRecords", "Groups", "Tags", "Roles", "UserAccesses",
                "PollCycles", "PollCycleDailySummaries", "ParseFailures", "ProcessedMessages",
                "UserAccessScopedGroups", "ApiKeys", "ApiKeyScopedGroups", "DomainGroup", "DomainTag", "AlertEvents", "AlertTickets", "DomainAlertStates", "DomainDkimRecords",
                "TlsrptReports", "TlsrptReportPolicies", "TlsrptFailureDetails", "AuditEntries", "BrandingImages", "GroupBrandings", "GroupReportSchedules", "ClientReportDeliveries", "UserDashboardPreferences", "SenderDecisions", "AlertRules", "AlertRuleScopeEntries", "AlertDestinations", "AlertRuleDestinations", "AlertRuleEscalationDestinations"
            RESTART IDENTITY CASCADE
            """,
            cancellationToken);

    internal static async Task WriteAsync(DotMarcDbContext context, DemoDataset dataset, CancellationToken cancellationToken)
    {
        var adminRole = new Role { Name = "Admin", IsLocked = true, IsScopable = false, Permissions = [.. Enum.GetValues<Permission>()] };
        var viewerRole = new Role { Name = "Viewer", IsLocked = false, IsScopable = true, Permissions = AccessBootstrapper.ViewerPermissions };
        context.Roles.AddRange(adminRole, viewerRole);

        var groupsByName = dataset.Groups.ToDictionary(g => g.Name, g => new Group { Name = g.Name, TargetDmarcPolicy = g.TargetPolicy });
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
                DmarcCheckDetail = domainSeed.DmarcCheckDetail,
                MtaStsEnabled = domainSeed.MtaStsEnabled,
                MtaStsMode = domainSeed.MtaStsMode,
                MtaStsStatus = domainSeed.MtaStsStatus,
                MtaStsCheckedUtc = domainSeed.MtaStsCheckedUtc,
                MtaStsCheckDetail = domainSeed.MtaStsCheckDetail,
                MtaStsMaxAgeSeconds = domainSeed.MtaStsMaxAgeSeconds,
                MtaStsMxHosts = domainSeed.MtaStsMxHosts,
                TlsrptCheckStatus = domainSeed.TlsrptCheckStatus,
                TlsrptCheckedUtc = domainSeed.TlsrptCheckedUtc,
                TlsrptCheckDetail = domainSeed.TlsrptCheckDetail,
                DmarcAuthorizationCheckStatus = domainSeed.DmarcAuthorizationCheckStatus,
                DmarcAuthorizationCheckedUtc = domainSeed.DmarcAuthorizationCheckStatus == DmarcAuthorizationCheckStatus.NotChecked ? null : domainSeed.FirstSeenUtc,
                DmarcAuthorizationCheckDetail = domainSeed.DmarcAuthorizationCheckDetail,
                SpfCheckStatus = domainSeed.SpfCheckStatus,
                SpfCheckedUtc = domainSeed.SpfCheckStatus == SpfCheckStatus.NotChecked ? null : domainSeed.FirstSeenUtc,
                SpfCheckDetail = domainSeed.SpfCheckDetail,
                SpfRangesJson = domainSeed.SpfRangesJson,
                MxCheckStatus = domainSeed.MxCheckStatus,
                MxCheckedUtc = domainSeed.MxCheckStatus == MxCheckStatus.NotChecked ? null : domainSeed.FirstSeenUtc,
                MxCheckDetail = domainSeed.MxCheckDetail,
                DkimSelectors = domainSeed.DkimSelectors ?? [],
                DkimCheckStatus = domainSeed.DkimCheckStatus,
                DkimCheckedUtc = domainSeed.DkimCheckStatus == DkimCheckStatus.NotConfigured ? null : domainSeed.FirstSeenUtc,
                DkimCheckDetail = domainSeed.DkimCheckDetail,
                DnsProvider = domainSeed.DnsProvider,
                DnsProviderCheckedUtc = domainSeed.DnsProvider == DetectedDnsProvider.NotChecked ? null : domainSeed.FirstSeenUtc,
                DnsZone = domainSeed.DnsZone,
                DnsNameservers = domainSeed.DnsNameservers ?? [],
                DmarcPolicy = domainSeed.DmarcPolicy,
                DmarcSubdomainPolicy = domainSeed.DmarcSubdomainPolicy,
                DmarcPercent = domainSeed.DmarcPercent,
                DmarcNonExistentSubdomainPolicy = domainSeed.DmarcNonExistentSubdomainPolicy,
                DmarcTesting = domainSeed.DmarcTesting,
                DmarcRecords = domainSeed.DmarcRecords ?? [],
                DmarcProblems = domainSeed.DmarcProblems ?? []
            };

            if (domainSeed.GroupName is not null)
            {
                domain.Groups.Add(groupsByName[domainSeed.GroupName]);
            }

            foreach (var additionalGroupName in domainSeed.AdditionalGroupNames ?? [])
            {
                domain.Groups.Add(groupsByName[additionalGroupName]);
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
                    ReceivedUtc = reportSeed.DateRangeEndUtc,
                    // Demo reports have no real RawXml to backfill from - mark them handled
                    // immediately so the auth-detail backfill cycle never wastes a cycle trying
                    // (and failing) to re-parse this placeholder XML.
                    AuthDetailBackfilledUtc = reportSeed.DateRangeEndUtc
                };

                foreach (var recordSeed in reportSeed.Records)
                {
                    var record = new ReportRecord
                    {
                        Report = report,
                        SourceIp = recordSeed.SourceIp,
                        MessageCount = recordSeed.MessageCount,
                        Disposition = recordSeed.Disposition,
                        SpfResult = recordSeed.SpfResult,
                        DkimResult = recordSeed.DkimResult,
                        HeaderFrom = domainSeed.Name
                    };

                    foreach (var reasonSeed in recordSeed.OverrideReasons ?? [])
                    {
                        record.OverrideReasons.Add(new ReportRecordPolicyOverrideReason
                        {
                            Type = reasonSeed.Type,
                            Comment = reasonSeed.Comment
                        });
                    }

                    foreach (var detailSeed in recordSeed.AuthDetails ?? [])
                    {
                        record.AuthDetails.Add(new ReportRecordAuthDetail
                        {
                            Mechanism = detailSeed.Mechanism,
                            Domain = detailSeed.Domain,
                            Result = detailSeed.Result,
                            Selector = detailSeed.Selector,
                            Scope = detailSeed.Scope,
                            HumanResult = detailSeed.HumanResult
                        });
                    }

                    report.Records.Add(record);
                }

                domain.Reports.Add(report);
            }

            foreach (var tlsrptReportSeed in domainSeed.TlsrptReports)
            {
                var tlsrptReport = new TlsrptReport
                {
                    Domain = domain,
                    ReportingOrg = tlsrptReportSeed.ReportingOrg,
                    ReportId = tlsrptReportSeed.ReportId,
                    DateRangeBeginUtc = tlsrptReportSeed.DateRangeBeginUtc,
                    DateRangeEndUtc = tlsrptReportSeed.DateRangeEndUtc,
                    RawJson = "{\"demo\":\"no raw report retained\"}",
                    ReceivedUtc = tlsrptReportSeed.DateRangeEndUtc
                };

                foreach (var policySeed in tlsrptReportSeed.Policies)
                {
                    var policy = new TlsrptReportPolicy
                    {
                        TlsrptReport = tlsrptReport,
                        PolicyType = policySeed.PolicyType,
                        PolicyDomain = policySeed.PolicyDomain,
                        SuccessfulSessionCount = policySeed.SuccessfulSessionCount,
                        FailedSessionCount = policySeed.FailedSessionCount
                    };

                    foreach (var failureDetailSeed in policySeed.FailureDetails)
                    {
                        policy.FailureDetails.Add(new TlsrptFailureDetail
                        {
                            TlsrptReportPolicy = policy,
                            ResultType = failureDetailSeed.ResultType,
                            FailedSessionCount = failureDetailSeed.FailedSessionCount,
                            ReceivingMxHostname = failureDetailSeed.ReceivingMxHostname,
                            FailureReasonCode = failureDetailSeed.FailureReasonCode,
                            AdditionalInformation = failureDetailSeed.AdditionalInformation
                        });
                    }

                    tlsrptReport.Policies.Add(policy);
                }

                domain.TlsrptReports.Add(tlsrptReport);
            }

            context.Domains.Add(domain);
        }

        foreach (var stateSeed in dataset.AlertStates)
        {
            var domain = context.Domains.Local.Single(candidate => candidate.Name == stateSeed.DomainName);
            domain.AlertStates.Add(new DomainAlertState { Item = stateSeed.Item, HasPassed = stateSeed.HasPassed, Baseline = stateSeed.Baseline });
        }

        foreach (var alertSeed in dataset.AlertEvents)
        {
            context.AlertEvents.Add(new AlertEvent
            {
                DomainName = alertSeed.DomainName,
                AlertType = alertSeed.AlertType,
                Severity = alertSeed.Severity,
                Title = alertSeed.Title,
                Message = alertSeed.Message,
                IsResolved = alertSeed.IsResolved,
                CreatedUtc = alertSeed.CreatedUtc,
                ResolvedUtc = alertSeed.ResolvedUtc,
                // Seeded alerts count as already notified, so none of them shows as held by a quiet window.
                NotifiedUtc = alertSeed.CreatedUtc
            });
        }

        // A ticket in each PSA, so the Alerts page shows them all. Raised a couple of minutes after the alert, as the real flow does.
        foreach (var sample in DemoTickets.Samples)
        {
            var alert = context.AlertEvents.Local.Single(candidate => candidate.DomainName == sample.DomainName && candidate.AlertType == sample.AlertType);
            alert.Tickets.Add(new DotMarc.Psa.AlertTicket { Psa = sample.Psa, TicketId = sample.TicketId, IsOpen = sample.IsOpen, CreatedUtc = alert.CreatedUtc.AddMinutes(2) });
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

        // Decisions refer to groups and domains by id, so they are written once those have been saved. They are attributed to a
        // fictional person, never to a real sign-in.
        foreach (var decisionSeed in dataset.SenderDecisions ?? [])
        {
            context.SenderDecisions.Add(new SenderDecision
            {
                Scope = decisionSeed.Scope,
                GroupId = decisionSeed.GroupName is null ? null : groupsByName[decisionSeed.GroupName].Id,
                DomainId = decisionSeed.DomainName is null ? null : context.Domains.Local.Single(candidate => candidate.Name == decisionSeed.DomainName).Id,
                Kind = SenderDecisionKind.Service,
                Key = decisionSeed.Key,
                Status = decisionSeed.Status,
                Note = decisionSeed.Note,
                DecidedBy = "demo",
                DecidedByName = "Nova MSP admin",
                DecidedUtc = decisionSeed.DecidedUtc,
            });
        }

        context.UserAccesses.AddRange(
            new UserAccess { Email = AdminEmail, Role = adminRole },
            new UserAccess { Email = ViewerEmail, Role = viewerRole, ScopedGroups = [groupsByName[ViewerScopedGroupName]] },
            new UserAccess { Email = ClientEmail, Role = viewerRole, ScopedGroups = [groupsByName[ViewerScopedGroupName]], IsClientPortal = true });

        // The brand is a singleton row the truncate above leaves alone, so reset it here: the fictional MSP's brand,
        // with Aurora Retail's own name and colour layered over it for the Demo Client.
        var brand = await context.BrandingSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        brand.ProductName = "Nova MSP";
        brand.PrimaryColour = "#0B5FFF";
        brand.SecondaryColour = "#FF6B00";
        brand.DarkPrimaryColour = null;
        brand.DarkSecondaryColour = null;
        brand.LightBackgroundColour = null;
        brand.LightSurfaceColour = null;
        brand.DarkBackgroundColour = null;
        brand.DarkSurfaceColour = null;
        brand.LogoImageId = null;
        brand.DarkLogoImageId = null;
        brand.SupportEmail = "help@nova-msp.example";
        brand.SupportUrl = "https://nova-msp.example/support";
        brand.SupportPhone = null;
        brand.FooterText = null;
        context.GroupBrandings.Add(new DotMarc.Portal.GroupBranding
        {
            GroupId = groupsByName[ViewerScopedGroupName].Id, DisplayName = "Aurora Retail Ltd", PrimaryColour = "#7A1FA2", DarkPrimaryColour = "#CE93D8",
        });

        // The DNS record settings are a singleton row too: put the system DMARC target back to the demo's, so a visitor's change doesn't stay.
        var dnsRecordSettings = await context.DnsRecordSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        dnsRecordSettings.TargetDmarcPolicy = dataset.SystemTargetPolicy;
        dnsRecordSettings.TargetDmarcSubdomainPolicy = null;

        // Report and email settings are singleton rows the truncate leaves alone too; put them back so one visitor's
        // changes don't stay for everyone. The demo never sends email whatever these say.
        var reportSettings = await context.ReportSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        reportSettings.TimeZoneId = "UTC";
        reportSettings.SendHour = 6;
        reportSettings.NumberFormat = DotMarc.Reporting.ClientReports.ReportSettings.DefaultNumberFormat;
        var emailSettings = await context.EmailSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        emailSettings.Provider = DotMarc.Email.EmailProvider.Off;
        emailSettings.FromAddress = null;
        emailSettings.FromName = null;
        emailSettings.SmtpHost = null;
        emailSettings.SmtpPort = 587;
        emailSettings.SmtpSecurity = DotMarc.Email.SmtpSecurity.StartTls;
        emailSettings.SmtpUsername = null;
        emailSettings.SmtpPasswordConfigured = false;

        // A monthly report schedule, so the Reports dialog has something to show. The demo never sends email.
        context.GroupReportSchedules.Add(new DotMarc.Reporting.ClientReports.GroupReportSchedule
        {
            GroupId = groupsByName[ViewerScopedGroupName].Id, Frequency = DotMarc.Reporting.ClientReports.ReportFrequency.Monthly,
            Recipients = ["reports@aurora-retail.example"], StartedUtc = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The default pack of alert rules (which also hands the alerts above to their default rules), and one rule beside it for the
        // demo to show: Aurora Retail reports weekly, so it is told about a missing report after 5 days, not the default 2.
        await AlertRuleSeeder.EnsureDefaultPackAsync(context, cancellationToken).ConfigureAwait(false);
        context.AlertRules.Add(new AlertRule
        {
            Kind = AlertTypes.MissedReport,
            Name = "Missing report: Aurora Retail reports weekly",
            Severity = "Warning",
            Position = -10,
            StopProcessing = true,
            // Quiet overnight: a missing report found at 3am is held until 07:00 rather than posted then.
            QuietMode = AlertQuietMode.Hold,
            QuietStartMinutes = 22 * 60,
            QuietEndMinutes = 7 * 60,
            ParametersJson = "{\"days\":\"5\"}",
            Scope = [new AlertRuleScopeEntry { Target = AlertRuleScopeTarget.Group, TargetId = groupsByName[ViewerScopedGroupName].Id }],
        });

        // And an Under review example: Cobalt Freight's senders sit Under review for weeks, so it is reminded after 14 days.
        context.AlertRules.Add(new AlertRule
        {
            Kind = AlertTypes.NewUnreviewedSender,
            Name = "New sender: Cobalt Freight reviews slowly",
            Severity = "Warning",
            Position = -10,
            StopProcessing = true,
            ParametersJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [AlertRuleParameterKeys.NewDays] = "7",
                [AlertRuleParameterKeys.MinimumMessages] = "20",
                [AlertRuleParameterKeys.UnderReview] = "afterDays",
                [AlertRuleParameterKeys.UnderReviewDays] = "14",
            }),
            Scope = [new AlertRuleScopeEntry { Target = AlertRuleScopeTarget.Group, TargetId = groupsByName["Cobalt Freight"].Id }],
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
