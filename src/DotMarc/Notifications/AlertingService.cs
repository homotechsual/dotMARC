using DotMarc.Baselines;
using DotMarc.Dns;
using System.Globalization;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.IpEnrichment;
using DotMarc.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotMarc.Notifications;

public interface IAlertingService
{
    Task CheckPinnedDomainsAsync(CancellationToken cancellationToken = default);
    Task ResolveDomainAlertAsync(string domainName, CancellationToken cancellationToken = default);
    Task HandleTlsrptReportAsync(string domainName, long failedSessionCount, IReadOnlyList<string> failureTypes, CancellationToken cancellationToken = default);
    Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, ReasonBreakdown reasonBreakdown, CancellationToken cancellationToken = default);
    Task RaiseClientReportFailedAsync(int groupId, string groupName, string periodLabel, string error, CancellationToken cancellationToken = default);
    Task ResolveClientReportFailedAsync(int groupId, CancellationToken cancellationToken = default);
    Task RaisePsaConnectionLostAsync(DotMarc.Psa.PsaKind psa, string reason, CancellationToken cancellationToken = default);
    Task ResolvePsaConnectionLostAsync(DotMarc.Psa.PsaKind psa, CancellationToken cancellationToken = default);
}

public sealed class AlertingService : IAlertingService
{
    private readonly IDbContextFactory<DotMarcDbContext> _dbFactory;
    private readonly IAlertWebhookClient _alertWebhookClient;
    private readonly IPsaTicketService _psaTicketService;
    private readonly ILogger<AlertingService> _logger;
    private readonly TimeProvider _timeProvider;

    public AlertingService(IDbContextFactory<DotMarcDbContext> dbFactory, IAlertWebhookClient alertWebhookClient, IPsaTicketService psaTicketService, ILogger<AlertingService> logger, TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _alertWebhookClient = alertWebhookClient;
        _psaTicketService = psaTicketService;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task CheckPinnedDomainsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Read live rather than once at startup: this is a singleton service, and settings are
        // now editable at any time from /alerts/settings (see NotificationSettings's doc
        // comment) - a value bound once via IOptions would never pick up a later change without
        // a restart.
        var settings = await NotificationSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        var rules = await AlertRuleSet.LoadAsync(db, cancellationToken).ConfigureAwait(false);
        var reasonWindowCutoffUtc = DomainStatistics.GetWindowCutoffUtc();
        var domains = await db.Domains
            .AsNoTracking()
            .Where(d => d.IsMonitored)
            .Include(d => d.Groups)
            .Include(d => d.Tags)
            .Include(d => d.Reports.Where(r => r.ReceivedUtc >= reasonWindowCutoffUtc))
            .ThenInclude(r => r.Records)
            .ThenInclude(rec => rec.OverrideReasons)
            .Include(d => d.Reports.Where(r => r.ReceivedUtc >= reasonWindowCutoffUtc))
            .ThenInclude(r => r.Records)
            .ThenInclude(rec => rec.AuthDetails)
            // Sibling collections under each record: split so they aren't joined into one
            // row-multiplying result that repeats every report's RawXml.
            .AsSplitQuery()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var openAlerts = await LoadOpenAlertsAsync(db, CycleKinds, cancellationToken).ConfigureAwait(false);
        var senderAlerts = await LoadSenderAlertContextAsync(db, rules, domains, cancellationToken).ConfigureAwait(false);
        var applicable = new ApplicableRules();

        foreach (var domain in domains)
        {
            var facts = FactsFor(domain);
            applicable.Record(domain.Name, AlertTypes.TlsrptFailure, rules.For(AlertTypes.TlsrptFailure, facts));
            applicable.Record(domain.Name, AlertTypes.UnexpectedActivityOnNullRoutedDomain, rules.For(AlertTypes.UnexpectedActivityOnNullRoutedDomain, facts));
            var missedRules = applicable.Record(domain.Name, AlertTypes.MissedReport, rules.For(AlertTypes.MissedReport, facts));
            if (domain.SpfCheckStatus == SpfCheckStatus.NullSpf)
            {
                // Null-routed (SPF v=spf1 -all): no reports is the expected, healthy state, not a
                // problem - resolve any pre-existing alert from before the domain became
                // null-routed and skip the missing-report check entirely for it.
                await ResolveDomainAlertAsync(domain.Name, cancellationToken).ConfigureAwait(false);

                // UnexpectedActivityOnNullRoutedDomain resolves only once the domain has gone
                // quiet again (no report within the missing-report threshold window). This branch
                // runs unconditionally for every null-routed domain each cycle, so resolving it
                // unconditionally here would auto-close the alert on the very next poll after it
                // fires, defeating its purpose of surfacing unexpected activity for an admin to
                // see. Same threshold semantics as MissedReport, just inverted: fires when a
                // report unexpectedly arrives, stays open while reports keep arriving within the
                // threshold window, auto-resolves once activity stops for the threshold period.
                var quietCutoffUtc = DateTimeOffset.UtcNow.AddDays(-(missedRules.Count > 0
                    ? missedRules[0].Int(AlertRuleParameterKeys.MissingDays)
                    : AlertRuleKinds.DefaultInt(AlertTypes.MissedReport, AlertRuleParameterKeys.MissingDays)));
                if (domain.LastReportReceivedUtc is null || domain.LastReportReceivedUtc < quietCutoffUtc)
                {
                    await ResolveAlertAsync(domain.Name, AlertTypes.UnexpectedActivityOnNullRoutedDomain, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (missedRules.Count == 0)
            {
                // No rule applies to this domain, so nothing may stay open for it.
                if (openAlerts.HasAny(domain.Name, AlertTypes.MissedReport))
                {
                    await ResolveDomainAlertAsync(domain.Name, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                foreach (var rule in missedRules)
                {
                    var cutoffUtc = DateTimeOffset.UtcNow.AddDays(-rule.Int(AlertRuleParameterKeys.MissingDays));
                    if (domain.LastReportReceivedUtc is { } lastReport && lastReport >= cutoffUtc)
                    {
                        if (openAlerts.Has(domain.Name, AlertTypes.MissedReport, rule.RuleId))
                        {
                            await ResolveAlertAsync(domain.Name, AlertTypes.MissedReport, rule.RuleId, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        var message = domain.LastReportReceivedUtc is { } receivedUtc
                            ? $"The monitored domain '{domain.Name}' has not received a DMARC report since {receivedUtc:O}."
                            : $"The monitored domain '{domain.Name}' has not received a DMARC report yet.";
                        await EnsureAlertAsync(db, settings, rule, domain.Name, "Missing expected DMARC report", message, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            // Independent of the report-freshness branch above - a domain can be reporting fine
            // AND have a reject mix worth flagging, so this always runs.
            await CheckSuspiciousRejectActivityAsync(db, settings, rules, domain, facts, openAlerts, applicable, cancellationToken).ConfigureAwait(false);

            await CheckSenderAlertsAsync(db, settings, rules, domain, facts, senderAlerts, openAlerts, applicable, cancellationToken).ConfigureAwait(false);

            await CheckMetricAlertsAsync(db, settings, rules, domain, facts, openAlerts, applicable, cancellationToken).ConfigureAwait(false);
        }

        // A rule that was disabled, moved out of scope or put behind a rule that stops processing no longer applies to a
        // domain, so what it left open there closes. Only for domains still monitored: the rest keep their alerts as before.
        var monitoredNames = domains.Select(domain => domain.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var systemAlert in openAlerts.All.Where(alert => SystemKinds.Contains(alert.Kind)))
        {
            applicable.Record(systemAlert.Subject, systemAlert.Kind, rules.For(systemAlert.Kind, facts: null));
        }

        await ResolveOrphanedAlertsAsync(
            openAlerts.All.Where(alert => monitoredNames.Contains(alert.Subject) || SystemKinds.Contains(alert.Kind)), applicable, cancellationToken).ConfigureAwait(false);

        await CheckDnsHealthAsync(db, settings, rules, domains, cancellationToken).ConfigureAwait(false);
        await CheckApiKeyExpiryAsync(db, settings, rules, cancellationToken).ConfigureAwait(false);

        // Last, so an alert the checks above resolved is never sent after all, and one raised in this very cycle is considered too.
        await SendHeldAlertsAsync(db, settings, cancellationToken).ConfigureAwait(false);
        await EscalateAlertsAsync(db, settings, cancellationToken).ConfigureAwait(false);
    }

    private static readonly string[] SystemKinds = [AlertTypes.ClientReportFailed, AlertTypes.PsaConnectionLost, AlertTypes.ApiKeyExpiring];

    /// <summary>The kinds whose rule changes the cycle reconciles: the ones it raises itself, plus the ones raised by events, so an
    /// alert closes when its rule stops applying whichever way it was raised.</summary>
    private static readonly string[] MetricKinds = [AlertTypes.PassRateLow, AlertTypes.VolumeChanged, AlertTypes.RejectedMessagesHigh];

    private static readonly string[] CycleKinds =
    [
        AlertTypes.MissedReport, AlertTypes.SuspiciousRejectActivity, AlertTypes.NewUnreviewedSender, AlertTypes.UnauthorisedSenderDelivered,
        AlertTypes.TlsrptFailure, AlertTypes.UnexpectedActivityOnNullRoutedDomain, .. MetricKinds, .. SystemKinds
    ];

    private static DomainFacts FactsFor(Domain domain) =>
        new(domain.Id, domain.Groups.Select(group => group.Id).ToHashSet(), domain.Tags.Select(tag => tag.Id).ToHashSet());

    /// <summary>The rule ids that applied to each domain and kind in this cycle, for finding the alerts whose rule no longer does.</summary>
    private sealed class ApplicableRules
    {
        private readonly Dictionary<(string Subject, string Kind), HashSet<int>> _ids = [];

        public IReadOnlyList<ResolvedAlertRule> Record(string subject, string kind, IReadOnlyList<ResolvedAlertRule> resolved)
        {
            _ids[(subject, kind)] = resolved.Select(rule => rule.RuleId).ToHashSet();
            return resolved;
        }

        public bool Contains(string subject, string kind, int ruleId) => _ids.TryGetValue((subject, kind), out var ids) && ids.Contains(ruleId);
    }

    private sealed record OpenAlert(string Subject, string Kind, int? RuleId);

    /// <summary>Which alerts of some kinds are open, read once per cycle so a quiet cycle costs one query rather than one per
    /// domain and kind.</summary>
    private sealed class OpenAlertIndex
    {
        private readonly HashSet<(string Subject, string Kind, int? RuleId)> _exact;
        private readonly HashSet<(string Subject, string Kind)> _any;

        public OpenAlertIndex(IReadOnlyList<OpenAlert> alerts)
        {
            All = alerts;
            _exact = alerts.Select(alert => (alert.Subject, alert.Kind, alert.RuleId)).ToHashSet();
            _any = alerts.Select(alert => (alert.Subject, alert.Kind)).ToHashSet();
        }

        public IReadOnlyList<OpenAlert> All { get; }
        public bool Has(string subject, string kind, int ruleId) => _exact.Contains((subject, kind, ruleId));
        public bool HasAny(string subject, string kind) => _any.Contains((subject, kind));
    }

    private static async Task<OpenAlertIndex> LoadOpenAlertsAsync(DotMarcDbContext db, IReadOnlyCollection<string> kinds, CancellationToken cancellationToken)
    {
        var open = await db.AlertEvents.AsNoTracking()
            .Where(alert => !alert.IsResolved && kinds.Contains(alert.AlertType))
            .Select(alert => new OpenAlert(alert.DomainName, alert.AlertType, alert.AlertRuleId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new OpenAlertIndex(open);
    }

    /// <summary>Closes every open alert whose rule did not apply to its subject in this cycle. Alerts with no rule are left to the
    /// code that raised them.</summary>
    private async Task ResolveOrphanedAlertsAsync(IEnumerable<OpenAlert> openAlerts, ApplicableRules applicable, CancellationToken cancellationToken)
    {
        var orphans = openAlerts
            .Where(alert => alert.RuleId is { } ruleId && !applicable.Contains(alert.Subject, alert.Kind, ruleId))
            .Distinct()
            .ToList();
        foreach (var orphan in orphans)
        {
            await ResolveAllCopiesAsync(orphan.Subject, orphan.Kind, orphan.RuleId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>What the sender alerts need that is the same for every domain in a cycle: every decision (a small table) and the
    /// cached IP owners for the IPs in the window. Nothing is loaded when no sender rule is on.</summary>
    private sealed record SenderAlertContext(List<SenderDecision> Decisions, Dictionary<string, IpInfo> CachedInfo, List<IpRange> Ranges);

    private async Task<SenderAlertContext> LoadSenderAlertContextAsync(DotMarcDbContext db, AlertRuleSet rules, List<Domain> domains, CancellationToken cancellationToken)
    {
        if (!rules.Any(AlertTypes.NewUnreviewedSender) && !rules.Any(AlertTypes.UnauthorisedSenderDelivered))
        {
            return new SenderAlertContext([], [], []);
        }

        var decisions = await db.SenderDecisions.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var ips = domains.SelectMany(domain => domain.Reports).SelectMany(report => report.Records).Select(record => record.SourceIp).Distinct().ToList();
        var cached = ips.Count == 0 ? [] : await IpInfoService.GetCachedAsync(db, ips, cancellationToken).ConfigureAwait(false);
        var ranges = await IpInfoService.GetCachedRangesAsync(db, cancellationToken).ConfigureAwait(false);
        return new SenderAlertContext(decisions, cached, ranges);
    }

    /// <summary>Raises or resolves the two sender alerts for one domain, once per rule that applies. A new sender is one first seen
    /// within the rule's number of days, so a domain's existing senders never alert. Null-routed domains get neither, since they
    /// have their own alert.</summary>
    private async Task CheckSenderAlertsAsync(
        DotMarcDbContext db, NotificationSettings settings, AlertRuleSet rules, Domain domain, DomainFacts facts, SenderAlertContext context,
        OpenAlertIndex openAlerts, ApplicableRules applicable, CancellationToken cancellationToken)
    {
        var applies = domain.SpfCheckStatus != SpfCheckStatus.NullSpf;
        var newRules = applicable.Record(domain.Name, AlertTypes.NewUnreviewedSender, applies ? rules.For(AlertTypes.NewUnreviewedSender, facts) : []);
        var deliveredRules = applicable.Record(domain.Name, AlertTypes.UnauthorisedSenderDelivered, applies ? rules.For(AlertTypes.UnauthorisedSenderDelivered, facts) : []);

        var nowUtc = DateTimeOffset.UtcNow;
        var results = new Dictionary<(int NewDays, int NewMinimum, int DeliveredMinimum, UnderReviewHandling Handling, bool NeedsNewIps), SenderAlertResult>();
        var newIpsByDays = new Dictionary<int, HashSet<string>>();
        Dictionary<string, string?>? ownerByIp = null;

        async Task<HashSet<string>> NewIpsAsync(int newDays)
        {
            if (!newIpsByDays.TryGetValue(newDays, out var newIps))
            {
                var newSinceUtc = nowUtc.AddDays(-newDays);
                newIps = new HashSet<string>(StringComparer.Ordinal);
                // A source that looks new inside the window may have reported before it. Only these few candidates cost a query.
                foreach (var ip in SenderAlertEvaluator.NewSourceCandidates(domain.Reports.ToList(), newSinceUtc))
                {
                    var hadEarlierReport = await db.ReportRecords
                        .AnyAsync(record => record.Report.DomainId == domain.Id && record.SourceIp == ip && record.Report.ReceivedUtc < newSinceUtc, cancellationToken)
                        .ConfigureAwait(false);
                    if (!hadEarlierReport)
                    {
                        newIps.Add(ip);
                    }
                }

                newIpsByDays[newDays] = newIps;
            }

            return newIps;
        }

        async Task<SenderAlertResult> ResultAsync(int newDays, int newMinimum, int deliveredMinimum, UnderReviewHandling handling, bool needsNewIps)
        {
            // A result that also worked out the new senders answers a delivered-only question too, so reuse it.
            if (results.TryGetValue((newDays, newMinimum, deliveredMinimum, handling, true), out var withNewIps))
            {
                return withNewIps;
            }

            if (results.TryGetValue((newDays, newMinimum, deliveredMinimum, handling, needsNewIps), out var existing))
            {
                return existing;
            }

            ownerByIp ??= IpInfoService.OwnerByIp(
                context.CachedInfo, context.Ranges, domain.Reports.SelectMany(report => report.Records).Select(record => record.SourceIp).Distinct(), nowUtc);
            var newIps = needsNewIps ? await NewIpsAsync(newDays).ConfigureAwait(false) : new HashSet<string>(StringComparer.Ordinal);
            var result = SenderAlertEvaluator.Evaluate(new SenderAlertInputs(
                domain.Id, domain.Groups.Select(group => group.Id).ToList(), domain.Reports.ToList(), context.Decisions, ownerByIp, newIps,
                new SenderAlertSettings(newMinimum, deliveredMinimum),
                SpfServiceRanges.Parse(domain.SpfRangesJson), handling, nowUtc));
            results[(newDays, newMinimum, deliveredMinimum, handling, needsNewIps)] = result;
            return result;
        }

        // What the other kind's side of a shared evaluation uses: the kinds' own defaults, so a new sender rule and a delivered rule
        // with default values share one evaluation.
        var defaultNewDays = AlertRuleKinds.DefaultInt(AlertTypes.NewUnreviewedSender, AlertRuleParameterKeys.NewDays);
        var defaultNewMinimum = AlertRuleKinds.DefaultInt(AlertTypes.NewUnreviewedSender, AlertRuleParameterKeys.MinimumMessages);
        var defaultDeliveredMinimum = AlertRuleKinds.DefaultInt(AlertTypes.UnauthorisedSenderDelivered, AlertRuleParameterKeys.MinimumMessages);

        if (newRules.Count == 0 && openAlerts.HasAny(domain.Name, AlertTypes.NewUnreviewedSender))
        {
            await ResolveAlertAsync(domain.Name, AlertTypes.NewUnreviewedSender, cancellationToken).ConfigureAwait(false);
        }

        foreach (var rule in newRules)
        {
            var newDays = Math.Clamp(rule.Int(AlertRuleParameterKeys.NewDays), 1, 30);
            var minimum = rule.Int(AlertRuleParameterKeys.MinimumMessages);
            var result = await ResultAsync(newDays, minimum, defaultDeliveredMinimum, rule.UnderReview(), needsNewIps: true).ConfigureAwait(false);
            if (result.NewUnreviewed.Count > 0)
            {
                await EnsureAlertAsync(db, settings, rule, domain.Name, SenderAlertMessages.NewSendersTitle,
                    SenderAlertMessages.NewSenders(domain.Name, result.NewUnreviewed), cancellationToken).ConfigureAwait(false);
            }
            else if (openAlerts.Has(domain.Name, AlertTypes.NewUnreviewedSender, rule.RuleId))
            {
                await ResolveAlertAsync(domain.Name, AlertTypes.NewUnreviewedSender, rule.RuleId, cancellationToken).ConfigureAwait(false);
            }
        }

        if (deliveredRules.Count == 0 && openAlerts.HasAny(domain.Name, AlertTypes.UnauthorisedSenderDelivered))
        {
            await ResolveAlertAsync(domain.Name, AlertTypes.UnauthorisedSenderDelivered, cancellationToken).ConfigureAwait(false);
        }

        foreach (var rule in deliveredRules)
        {
            var minimum = rule.Int(AlertRuleParameterKeys.MinimumMessages);
            var result = await ResultAsync(defaultNewDays, defaultNewMinimum, minimum, UnderReviewHandling.Default, needsNewIps: false).ConfigureAwait(false);
            if (result.UnauthorisedDelivered.Count > 0)
            {
                await EnsureAlertAsync(db, settings, rule, domain.Name, SenderAlertMessages.UnauthorisedDeliveredTitle,
                    SenderAlertMessages.UnauthorisedDelivered(domain.Name, result.UnauthorisedDelivered, domain.DmarcPolicy ?? DmarcPolicyLevel.None), cancellationToken).ConfigureAwait(false);
            }
            else if (openAlerts.Has(domain.Name, AlertTypes.UnauthorisedSenderDelivered, rule.RuleId))
            {
                await ResolveAlertAsync(domain.Name, AlertTypes.UnauthorisedSenderDelivered, rule.RuleId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Runs DnsHealthAlertEvaluator for every monitored domain, saves what it remembers, then raises and
    /// resolves alerts. Resolves are only sent for alerts that are open, so a quiet cycle costs one query, not one per
    /// domain per check.</summary>
    private async Task CheckDnsHealthAsync(DotMarcDbContext db, NotificationSettings settings, AlertRuleSet rules, List<Domain> domains, CancellationToken cancellationToken)
    {
        await AutoCloseAcknowledgeableAlertsAsync(rules, cancellationToken).ConfigureAwait(false);

        var nowUtc = DateTimeOffset.UtcNow;
        var domainIds = domains.Select(domain => domain.Id).ToList();
        var states = await db.DomainAlertStates
            .Where(state => domainIds.Contains(state.DomainId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var openAlerts = await LoadOpenAlertsAsync(db, AlertTypes.DnsHealth, cancellationToken).ConfigureAwait(false);
        var recordSettings = await DnsRecordSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
        var applicable = new ApplicableRules();

        var actionsByDomain = new List<(Domain Domain, IReadOnlyList<DnsHealthAlertAction> Actions)>();
        foreach (var domain in domains)
        {
            var domainStates = states.Where(state => state.DomainId == domain.Id).ToList();
            actionsByDomain.Add((domain, DnsHealthAlertEvaluator.Evaluate(domain, domainStates, rules, nowUtc, DmarcTargetResolver.Resolve(domain, recordSettings))));
            db.DomainAlertStates.AddRange(domainStates.Where(state => state.Id == 0));

            var facts = FactsFor(domain);
            foreach (var kind in AlertTypes.DnsHealth)
            {
                applicable.Record(domain.Name, kind, rules.For(kind, facts));
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (domain, actions) in actionsByDomain)
        {
            foreach (var action in actions)
            {
                if (action.Kind == DnsHealthActionKind.Raise)
                {
                    if (AlertAcknowledgement.IsAcknowledgeable(action.AlertType)
                        && !await IsStillPendingAsync(db, domain.Id, action.AlertType, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    await EnsureAlertAsync(db, settings, action.Rule!, domain.Name, action.Title, action.Message, cancellationToken).ConfigureAwait(false);
                }
                else if (action.RuleId is { } ruleId
                    ? openAlerts.Has(domain.Name, action.AlertType, ruleId)
                    : openAlerts.HasAny(domain.Name, action.AlertType))
                {
                    await ResolveAllCopiesAsync(domain.Name, action.AlertType, action.RuleId, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // A domain that stops being monitored isn't evaluated any more, so close what it left open. For the ones still monitored,
        // close what a rule left open once that rule no longer applies to the domain.
        var monitoredNames = domains.Select(domain => domain.Name).ToHashSet(StringComparer.Ordinal);
        var unmonitored = openAlerts.All.Where(alert => !monitoredNames.Contains(alert.Subject)).Select(alert => (alert.Subject, alert.Kind)).Distinct().ToList();
        foreach (var (subject, kind) in unmonitored)
        {
            await ResolveAllCopiesAsync(subject, kind, cancellationToken).ConfigureAwait(false);
        }

        await ResolveOrphanedAlertsAsync(openAlerts.All.Where(alert => monitoredNames.Contains(alert.Subject)), applicable, cancellationToken).ConfigureAwait(false);
    }

    private async Task CheckSuspiciousRejectActivityAsync(
        DotMarcDbContext context, NotificationSettings settings, AlertRuleSet rules, Domain domain, DomainFacts facts, OpenAlertIndex openAlerts, ApplicableRules applicable, CancellationToken cancellationToken)
    {
        var suspiciousRules = applicable.Record(domain.Name, AlertTypes.SuspiciousRejectActivity, rules.For(AlertTypes.SuspiciousRejectActivity, facts));
        if (suspiciousRules.Count == 0)
        {
            if (openAlerts.HasAny(domain.Name, AlertTypes.SuspiciousRejectActivity))
            {
                await ResolveAlertAsync(domain.Name, AlertTypes.SuspiciousRejectActivity, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var breakdown = DomainStatistics.GetReasonBreakdown(domain.Reports);
        var nonBenign = breakdown.LocalPolicy + breakdown.Other + breakdown.InferredSpfFailure + breakdown.InferredDkimFailure + breakdown.InferredBothFailure + breakdown.NoReasonGiven;
        var nonBenignPercent = breakdown.Total == 0 ? 0 : (double)nonBenign / breakdown.Total * 100;

        foreach (var rule in suspiciousRules)
        {
            var minimumVolume = rule.Int(AlertRuleParameterKeys.MinimumMessages);
            var minimumPercent = rule.Int(AlertRuleParameterKeys.NonBenignPercent);
            if (breakdown.Total >= minimumVolume && nonBenignPercent >= minimumPercent)
            {
                var message = $"'{domain.Name}' rejected/quarantined {breakdown.Total} message(s) in the last 30 days, and {nonBenignPercent:F0}% of those had no benign override reason (forwarder/mailing list/sampling) - this looks like more than benign forwarding." +
                    BuildInferredBreakdownClause(breakdown);
                await EnsureAlertAsync(context, settings, rule, domain.Name, "Reject activity looks like more than benign forwarding", message, cancellationToken).ConfigureAwait(false);
            }
            else if (openAlerts.Has(domain.Name, AlertTypes.SuspiciousRejectActivity, rule.RuleId))
            {
                await ResolveAlertAsync(domain.Name, AlertTypes.SuspiciousRejectActivity, rule.RuleId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Runs the three metric kinds for one domain. Each applicable rule is judged on its own settings: it raises its alert when
    /// the numbers say so and closes it again when they stop saying so. A kind with no applicable rule closes whatever it left open.</summary>
    private async Task CheckMetricAlertsAsync(
        DotMarcDbContext context, NotificationSettings settings, AlertRuleSet rules, Domain domain, DomainFacts facts, OpenAlertIndex openAlerts, ApplicableRules applicable, CancellationToken cancellationToken)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        foreach (var kind in MetricKinds)
        {
            var kindRules = applicable.Record(domain.Name, kind, rules.For(kind, facts));
            if (kindRules.Count == 0)
            {
                if (openAlerts.HasAny(domain.Name, kind))
                {
                    await ResolveAlertAsync(domain.Name, kind, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            foreach (var rule in kindRules)
            {
                if (EvaluateMetricRule(kind, rule, domain, nowUtc) is var (title, message))
                {
                    await EnsureAlertAsync(context, settings, rule, domain.Name, title, message, cancellationToken).ConfigureAwait(false);
                }
                else if (openAlerts.Has(domain.Name, kind, rule.RuleId))
                {
                    await ResolveAlertAsync(domain.Name, kind, rule.RuleId, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>The alert a metric rule raises for a domain, or null when its numbers are fine.</summary>
    private static (string Title, string Message)? EvaluateMetricRule(string kind, ResolvedAlertRule rule, Domain domain, DateTimeOffset nowUtc)
    {
        var windowDays = rule.Int(AlertRuleParameterKeys.WindowDays);
        var lastDays = windowDays == 1 ? "the last day" : $"the last {windowDays} days";
        switch (kind)
        {
            case AlertTypes.PassRateLow:
                var belowPercent = rule.Int(AlertRuleParameterKeys.BelowPercent);
                var passRate = MetricAlertEvaluator.PassRate(domain.Reports, windowDays, belowPercent, rule.Int(AlertRuleParameterKeys.MinimumMessages), nowUtc);
                return passRate.Raise
                    ? ("DMARC pass rate is low", $"{domain.Name}: DMARC pass rate was {passRate.PassPercent:F0}% over {lastDays} (threshold {belowPercent}%, {MessageCount(passRate.Messages)}).")
                    : null;
            case AlertTypes.VolumeChanged:
                var volume = MetricAlertEvaluator.Volume(domain.Reports, windowDays, rule.Text(AlertRuleParameterKeys.Direction), rule.Int(AlertRuleParameterKeys.ChangePercent), rule.Int(AlertRuleParameterKeys.MinimumMessages), nowUtc);
                var before = windowDays == 1 ? "the day before" : $"the {windowDays} days before";
                return volume.Raise
                    ? ("Message volume changed", $"{domain.Name}: message volume {(volume.ChangePercent < 0 ? "fell" : "rose")} {Math.Abs(volume.ChangePercent):F0}% over {lastDays} ({MessageCount(volume.Current)}, {volume.Previous} in {before}).")
                    : null;
            case AlertTypes.RejectedMessagesHigh:
                var threshold = rule.Int(AlertRuleParameterKeys.MessagesAtLeast);
                var rejected = MetricAlertEvaluator.Rejected(domain.Reports, windowDays, threshold, nowUtc);
                return rejected.Raise
                    ? ("Rejected or quarantined messages are high", $"{domain.Name}: {MessageCount(rejected.Messages)} {(rejected.Messages == 1 ? "was" : "were")} rejected or quarantined over {lastDays} (threshold {threshold}).")
                    : null;
            default:
                return null;
        }
    }

    private static string MessageCount(int messages) => messages == 1 ? "1 message" : $"{messages} messages";

    /// <summary>Appends a short "of that, X had inferred SPF/DKIM issues" clause when any of the
    /// three inferred buckets are non-zero, so the alert itself points at which mechanism to
    /// investigate first instead of sending the reader to the dashboard for that.</summary>
    private static string BuildInferredBreakdownClause(ReasonBreakdown breakdown)
    {
        var parts = new List<string>();
        if (breakdown.InferredSpfFailure > 0)
        {
            parts.Add($"{breakdown.InferredSpfFailure} an inferred SPF-only failure");
        }
        if (breakdown.InferredDkimFailure > 0)
        {
            parts.Add($"{breakdown.InferredDkimFailure} an inferred DKIM-only failure");
        }
        if (breakdown.InferredBothFailure > 0)
        {
            parts.Add($"{breakdown.InferredBothFailure} both SPF and DKIM failing or misaligned");
        }

        return parts.Count == 0 ? "" : $" Of that: {string.Join(", ", parts)}.";
    }

    public async Task ResolveDomainAlertAsync(string domainName, CancellationToken cancellationToken = default)
        => await ResolveAlertAsync(domainName, AlertTypes.MissedReport, cancellationToken).ConfigureAwait(false);

    public async Task HandleTlsrptReportAsync(string domainName, long failedSessionCount, IReadOnlyList<string> failureTypes, CancellationToken cancellationToken = default)
    {
        if (failedSessionCount == 0)
        {
            await ResolveAlertAsync(domainName, AlertTypes.TlsrptFailure, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await NotificationSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        var failureSummary = failureTypes.Count == 0 ? "no failure category supplied" : string.Join(", ", failureTypes.Distinct(StringComparer.OrdinalIgnoreCase));
        var message = $"TLSRPT reported {failedSessionCount} failed TLS delivery session(s) for '{domainName}'. Failure types: {failureSummary}.";
        var rules = await AlertRuleSet.LoadAsync(db, cancellationToken).ConfigureAwait(false);
        var facts = await LoadDomainFactsAsync(db, domainName, cancellationToken).ConfigureAwait(false);
        foreach (var rule in rules.For(AlertTypes.TlsrptFailure, facts))
        {
            await EnsureAlertAsync(db, settings, rule, domainName, "TLS delivery failures reported", message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The ids that decide which rules apply to a domain, found by name. A name that is not a known domain gets only the
    /// rules that apply to every domain.</summary>
    private static async Task<DomainFacts> LoadDomainFactsAsync(DotMarcDbContext db, string domainName, CancellationToken cancellationToken)
    {
        var domain = await db.Domains.AsNoTracking()
            .Include(candidate => candidate.Groups)
            .Include(candidate => candidate.Tags)
            .AsSplitQuery()
            .FirstOrDefaultAsync(candidate => candidate.Name == domainName, cancellationToken)
            .ConfigureAwait(false);
        return domain is null ? DomainFacts.None : FactsFor(domain);
    }

    public async Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, ReasonBreakdown reasonBreakdown, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await NotificationSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        // Benign-dominant (at least half the reject/quarantine volume has a forwarder/mailing-list/
        // sampling override reason) reads as "probably not an attack"; anything else, including no
        // reject/quarantine volume at all yet, adds no extra sentence rather than guessing.
        var reasonContext = reasonBreakdown.Total == 0
            ? ""
            : reasonBreakdown.BenignOverride * 2 >= reasonBreakdown.Total
                ? " This looks like a forwarder or mailing list, not spoofing."
                : " No benign override reason was given - this looks like a genuine spoofing attempt.";

        var message = $"'{domainName}' is marked null-routed (SPF v=spf1 -all - no authorized senders) but a DMARC aggregate report just arrived showing mail activity. This may be legitimate traffic that needs accounting for, or a spoofing attempt.{reasonContext}";
        var rules = await AlertRuleSet.LoadAsync(db, cancellationToken).ConfigureAwait(false);
        var facts = await LoadDomainFactsAsync(db, domainName, cancellationToken).ConfigureAwait(false);
        foreach (var rule in rules.For(AlertTypes.UnexpectedActivityOnNullRoutedDomain, facts))
        {
            await EnsureAlertAsync(db, settings, rule, domainName, "Unexpected mail activity on a null-routed domain", message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Whether a policy or nameserver change is still waiting, read fresh from the database. Someone may have
    /// acknowledged it (or closed its ticket) since this cycle read the state; raising it anyway would send a new
    /// notification and ticket seconds after they dismissed it.</summary>
    private static Task<bool> IsStillPendingAsync(DotMarcDbContext db, int domainId, string alertType, CancellationToken cancellationToken)
    {
        var item = alertType == AlertTypes.DmarcPolicyWeakened ? DnsHealthItems.DmarcPolicy : DnsHealthItems.Nameservers;
        return db.DomainAlertStates
            .AsNoTracking()
            .AnyAsync(state => state.DomainId == domainId && state.Item == item && state.PendingSinceUtc != null, cancellationToken);
    }

    /// <summary>Resolves every open copy of a DNS health alert. One left open past the cooldown is raised again as a new
    /// row with its own ticket, so after a long outage there can be several; they all close once the condition clears,
    /// not one per cycle.</summary>
    private async Task ResolveAllCopiesAsync(string domainName, string alertType, CancellationToken cancellationToken)
        => await ResolveAllCopiesAsync(domainName, alertType, ruleId: null, cancellationToken).ConfigureAwait(false);

    /// <summary>As above, for one rule, or for every rule when the rule id is null.</summary>
    private async Task ResolveAllCopiesAsync(string domainName, string alertType, int? ruleId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var openCopies = await db.AlertEvents
            .Where(e => e.DomainName == domainName && e.AlertType == alertType && !e.IsResolved && (ruleId == null || e.AlertRuleId == ruleId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var resolvedUtc = DateTimeOffset.UtcNow;
        foreach (var copy in openCopies)
        {
            copy.IsResolved = true;
            copy.ResolvedUtc = resolvedUtc;
            try
            {
                await _psaTicketService.CloseTicketsAsync(db, copy, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to close PSA ticket for {DomainName} alert {AlertType}.", copy.DomainName, copy.AlertType);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ResolveAlertAsync(string domainName, string alertType, CancellationToken cancellationToken)
        => await ResolveAlertAsync(domainName, alertType, ruleId: null, cancellationToken).ConfigureAwait(false);

    /// <summary>Resolves the latest open alert of the type for a subject, for one rule or (rule id null) for each rule.</summary>
    private async Task ResolveAlertAsync(string domainName, string alertType, int? ruleId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var openAlerts = await db.AlertEvents
            .Where(e => e.DomainName == domainName && e.AlertType == alertType && !e.IsResolved && (ruleId == null || e.AlertRuleId == ruleId))
            .OrderByDescending(e => e.CreatedUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var latestPerRule = openAlerts.GroupBy(alert => alert.AlertRuleId).Select(group => group.First()).ToList();
        if (latestPerRule.Count == 0)
        {
            return;
        }

        foreach (var activeAlert in latestPerRule)
        {
            activeAlert.IsResolved = true;
            activeAlert.ResolvedUtc = DateTimeOffset.UtcNow;

            try
            {
                await _psaTicketService.CloseTicketsAsync(db, activeAlert, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to close PSA ticket for {DomainName} alert {AlertType}.", activeAlert.DomainName, activeAlert.AlertType);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Raises an alert for a rule unless the same one is open and younger than the cooldown. The cooldown is the rule's
    /// own, else <paramref name="defaultCooldown"/>, else the settings' cooldown. The alert takes the rule's severity.</summary>
    private async Task EnsureAlertAsync(DotMarcDbContext context, NotificationSettings settings, ResolvedAlertRule rule, string subject, string title, string message, CancellationToken cancellationToken, TimeSpan? defaultCooldown = null)
    {
        var cooldown = rule.Rule.CooldownMinutes is { } ruleCooldownMinutes ? TimeSpan.FromMinutes(ruleCooldownMinutes) : defaultCooldown;
        await EnsureAlertCoreAsync(context, settings, rule.RuleId, rule.Severity, subject, rule.Rule.Kind, title, message, cancellationToken, cooldown).ConfigureAwait(false);
    }

    private async Task EnsureAlertCoreAsync(DotMarcDbContext context, NotificationSettings settings, int? ruleId, string severity, string domainName, string alertType, string title, string message, CancellationToken cancellationToken, TimeSpan? cooldown)
    {
        if (AlertTypes.Find(alertType) is null)
        {
            // A developer slip, not something to stop the cycle over: the alert is still raised (and creates a ticket,
            // the default), it just isn't listed on the ticket rule screens until it is added to AlertTypes.All.
            _logger.LogError("Alert type {AlertType} is not in AlertTypes.All, so it can't be controlled from the ticket rule screens. Add it there. The alert is still raised.", alertType);
        }

        var activeAlert = await context.AlertEvents
            .Where(e => e.DomainName == domainName && e.AlertType == alertType && e.AlertRuleId == ruleId && !e.IsResolved)
            .OrderByDescending(e => e.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (activeAlert is not null)
        {
            if (activeAlert.CreatedUtc > _timeProvider.GetUtcNow().Subtract(cooldown ?? TimeSpan.FromMinutes(settings.CooldownMinutes)))
            {
                return;
            }
        }

        var alert = new AlertEvent
        {
            DomainName = domainName,
            AlertType = alertType,
            Severity = severity,
            Title = title,
            Message = message,
            CreatedUtc = _timeProvider.GetUtcNow(),
            AlertRuleId = ruleId
        };

        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Quiet hours decide whether the notification goes now, waits (Hold: NotifiedUtc stays empty and the cycle sends it
            // once the window ends) or is muted. They affect the notification only: the alert is recorded and a ticket still opens.
            var delivery = await LoadDeliveryAsync(context, settings, cancellationToken).ConfigureAwait(false);
            var rule = ruleId is { } id ? delivery.Rules.GetValueOrDefault(id) : null;
            var decision = QuietHours.Decide(rule, severity, delivery.Now, delivery.Zone);
            if (decision == QuietDecision.Deliver)
            {
                await SendNotificationAsync(delivery, rule, alert, titlePrefix: null, cancellationToken).ConfigureAwait(false);
            }

            if (decision != QuietDecision.Hold)
            {
                alert.NotifiedUtc = delivery.Now;
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send notification for {DomainName} alert {AlertType}.", domainName, alertType);
        }

        try
        {
            await _psaTicketService.CreateTicketsAsync(context, alert, cancellationToken).ConfigureAwait(false);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create PSA ticket for {DomainName} alert {AlertType}.", domainName, alertType);
        }
    }

    /// <summary>Everything delivering a notification needs, read once: the rules with their destinations, every destination, the zone
    /// quiet hours are read in, and now.</summary>
    private sealed record DeliveryContext(Dictionary<int, AlertRule> Rules, IReadOnlyList<AlertDestination> Destinations, TimeZoneInfo Zone, DateTimeOffset Now, bool NotificationsEnabled);

    private async Task<DeliveryContext> LoadDeliveryAsync(DotMarcDbContext context, NotificationSettings settings, CancellationToken cancellationToken)
    {
        var rules = await context.AlertRules.AsNoTracking()
            .Include(rule => rule.Destinations).Include(rule => rule.EscalationDestinations).AsSplitQuery()
            .ToDictionaryAsync(rule => rule.Id, cancellationToken).ConfigureAwait(false);
        var destinations = await context.AlertDestinations.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return new DeliveryContext(rules, destinations, QuietHours.ZoneFor(settings.TimeZoneId), _timeProvider.GetUtcNow(), settings.Enabled);
    }

    /// <summary>Sends an alert to the destinations its rule chooses (to every destination for an alert with no rule), or to nobody while
    /// notifications are switched off. A rule that no longer exists sends to all.</summary>
    private async Task SendNotificationAsync(DeliveryContext delivery, AlertRule? rule, AlertEvent alert, string? titlePrefix, CancellationToken cancellationToken)
    {
        var destinations = delivery.NotificationsEnabled ? AlertDestinationRouting.Resolve(rule, delivery.Destinations) : [];
        await _alertWebhookClient.SendAlertAsync(destinations, alert.DomainName, alert.AlertType, alert.Severity, titlePrefix + alert.Title, alert.Message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends the notification of every open alert a quiet window was holding, once its window has ended. An alert resolved or
    /// acknowledged in the meantime is never sent.</summary>
    private async Task SendHeldAlertsAsync(DotMarcDbContext context, NotificationSettings settings, CancellationToken cancellationToken)
    {
        var held = await context.AlertEvents.Where(alert => !alert.IsResolved && alert.NotifiedUtc == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (held.Count == 0)
        {
            return;
        }

        var delivery = await LoadDeliveryAsync(context, settings, cancellationToken).ConfigureAwait(false);
        foreach (var alert in held)
        {
            var rule = alert.AlertRuleId is { } id ? delivery.Rules.GetValueOrDefault(id) : null;
            if (QuietHours.Decide(rule, alert.Severity, delivery.Now, delivery.Zone) != QuietDecision.Deliver)
            {
                continue;
            }

            try
            {
                await SendNotificationAsync(delivery, rule, alert, titlePrefix: null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Failed to send the held {AlertType} alert for {DomainName}.", alert.AlertType, alert.DomainName);
            }

            alert.NotifiedUtc = delivery.Now;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Sends an open alert to its rule's escalation destinations once it has stayed open for the rule's escalation time since it was
    /// notified. It happens once per alert, ignores quiet hours, and never happens to an alert still being held (not yet notified). Acknowledging
    /// an alert resolves it, so open means unacknowledged.</summary>
    private async Task EscalateAlertsAsync(DotMarcDbContext context, NotificationSettings settings, CancellationToken cancellationToken)
    {
        var candidates = await context.AlertEvents
            .Where(alert => !alert.IsResolved && alert.NotifiedUtc != null && alert.EscalatedUtc == null && alert.AlertRuleId != null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return;
        }

        var delivery = await LoadDeliveryAsync(context, settings, cancellationToken).ConfigureAwait(false);
        foreach (var alert in candidates)
        {
            if (delivery.Rules.GetValueOrDefault(alert.AlertRuleId!.Value) is not { EscalateAfterMinutes: { } minutes } rule
                || alert.NotifiedUtc!.Value.AddMinutes(minutes) > delivery.Now)
            {
                continue;
            }

            var escalateTo = AlertDestinationRouting.ResolveEscalation(rule, delivery.Destinations);
            if (escalateTo.Count > 0 && delivery.NotificationsEnabled)
            {
                try
                {
                    await _alertWebhookClient.SendAlertAsync(escalateTo, alert.DomainName, alert.AlertType, alert.Severity, "Escalated: " + alert.Title, alert.Message, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(exception, "Failed to escalate the {AlertType} alert for {DomainName}.", alert.AlertType, alert.DomainName);
                }
            }

            // Marked even when there was nobody to tell or the send failed, so one alert is never escalated again and again.
            alert.EscalatedUtc = delivery.Now;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>What a Group's failed report alert is about. Stored where a domain alert stores its domain name; it ends
    /// with the Group's id so the alert still resolves after the Group is renamed.</summary>
    public static string ClientReportAlertSubject(int groupId, string groupName) =>
        string.Create(CultureInfo.InvariantCulture, $"Client report for {groupName} (group {groupId})");

    private static string ClientReportSubjectSuffix(int groupId) => string.Create(CultureInfo.InvariantCulture, $"(group {groupId})");

    public async Task RaiseClientReportFailedAsync(int groupId, string groupName, string periodLabel, string error, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await NotificationSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        var rules = await AlertRuleSet.LoadAsync(db, cancellationToken).ConfigureAwait(false);
        foreach (var rule in rules.For(AlertTypes.ClientReportFailed, facts: null))
        {
            await EnsureAlertAsync(db, settings, rule, ClientReportAlertSubject(groupId, groupName), "Client report failed",
                $"The {periodLabel} report for {groupName} couldn't be sent for 24 hours. Last error: {error}", cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ResolveClientReportFailedAsync(int groupId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var suffix = ClientReportSubjectSuffix(groupId);
        var subjects = await db.AlertEvents
            .Where(alert => alert.AlertType == AlertTypes.ClientReportFailed && !alert.IsResolved && alert.DomainName.EndsWith(suffix))
            .Select(alert => alert.DomainName)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var subject in subjects)
        {
            await ResolveAllCopiesAsync(subject, AlertTypes.ClientReportFailed, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>What a PSA's lost-connection alert is about. Stored where a domain alert stores its domain name.</summary>
    public static string PsaConnectionAlertSubject(DotMarc.Psa.PsaKind psa) => $"{DotMarc.Psa.PsaKindNames.DisplayName(psa)} connection";

    public async Task RaisePsaConnectionLostAsync(DotMarc.Psa.PsaKind psa, string reason, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await NotificationSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        var name = DotMarc.Psa.PsaKindNames.DisplayName(psa);
        var rules = await AlertRuleSet.LoadAsync(db, cancellationToken).ConfigureAwait(false);
        foreach (var rule in rules.For(AlertTypes.PsaConnectionLost, facts: null))
        {
            await EnsureAlertAsync(db, settings, rule, PsaConnectionAlertSubject(psa), "PSA connection lost",
                $"dotMARC can no longer refresh its {name} sign-in ({reason}). New alerts won't raise {name} tickets until someone reconnects under Manage > Alerts & PSA > PSA > {name}.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ResolvePsaConnectionLostAsync(DotMarc.Psa.PsaKind psa, CancellationToken cancellationToken = default)
    {
        var subject = PsaConnectionAlertSubject(psa);
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            // Called every keep-alive run, so a quiet run costs one query.
            if (!await db.AlertEvents.AnyAsync(alert => alert.AlertType == AlertTypes.PsaConnectionLost && alert.DomainName == subject && !alert.IsResolved, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        await ResolveAllCopiesAsync(subject, AlertTypes.PsaConnectionLost, cancellationToken).ConfigureAwait(false);
    }

    public static readonly TimeSpan ApiKeyExpiryWarning = TimeSpan.FromDays(14);

    /// <summary>What an expiring key's alert is about. Stored where a domain alert stores its domain name; a key's name
    /// and prefix never change, so the same key always has the same subject.</summary>
    public static string ApiKeyAlertSubject(ApiKey key) => $"API key {key.Name} ({key.Prefix})";

    /// <summary>Warns before an API key expires so whatever uses it doesn't break unannounced. An expiry is a known date,
    /// so it's announced once (the cooldown is the whole warning period) rather than every cooldown like a fault. The
    /// alert closes when the key is revoked or finally expires.</summary>
    private async Task CheckApiKeyExpiryAsync(DotMarcDbContext db, NotificationSettings settings, AlertRuleSet rules, CancellationToken cancellationToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var openSubjects = (await db.AlertEvents
            .Where(alert => alert.AlertType == AlertTypes.ApiKeyExpiring && !alert.IsResolved)
            .Select(alert => alert.DomainName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).ToHashSet();
        var keys = await db.ApiKeys.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var keyRules = rules.For(AlertTypes.ApiKeyExpiring, facts: null);
        foreach (var apiKey in keys)
        {
            var subject = ApiKeyAlertSubject(apiKey);
            var expiresSoon = apiKey.IsActive(nowUtc) && apiKey.ExpiresUtc - nowUtc <= ApiKeyExpiryWarning;
            if (expiresSoon)
            {
                foreach (var rule in keyRules)
                {
                    await EnsureAlertAsync(db, settings, rule, subject, "API key expiring soon",
                        $"The API key '{apiKey.Name}' ({apiKey.Prefix}...) expires on {apiKey.ExpiresUtc:yyyy-MM-dd}. Create a replacement on the Access page, switch whatever uses this key over to it, then revoke this one.",
                        cancellationToken, defaultCooldown: ApiKeyExpiryWarning).ConfigureAwait(false);
                }
            }
            else if (openSubjects.Contains(subject))
            {
                await ResolveAllCopiesAsync(subject, AlertTypes.ApiKeyExpiring, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static readonly AuditActor AutoCloseActor = AuditActor.ForSystem("Alert auto-close");

    /// <summary>Closes policy and nameserver alerts left open past their rule's auto-close days, as if acknowledged. A rule with none
    /// (or zero) never closes them.</summary>
    private async Task AutoCloseAcknowledgeableAlertsAsync(AlertRuleSet rules, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var open = await db.AlertEvents.AsNoTracking()
            .Where(alert => !alert.IsResolved && (alert.AlertType == AlertTypes.DmarcPolicyWeakened || alert.AlertType == AlertTypes.NameserversChanged))
            .Select(alert => new { alert.Id, alert.CreatedUtc, alert.AlertRuleId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var nowUtc = DateTimeOffset.UtcNow;
        var staleAlertIds = open
            .Where(alert =>
            {
                var days = alert.AlertRuleId is { } ruleId ? rules.Find(ruleId)?.AutoCloseDays : null;
                return days > 0 && alert.CreatedUtc < nowUtc.AddDays(-days.Value);
            })
            .Select(alert => alert.Id)
            .ToList();

        foreach (var alertId in staleAlertIds)
        {
            await using var alertContext = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var result = await AlertAcknowledgement.AcknowledgeAsync(alertContext, AutoCloseActor, alertId, _psaTicketService, cancellationToken).ConfigureAwait(false);
            if (result.Outcome == AcknowledgeOutcome.AcknowledgedButTicketNotClosed)
            {
                _logger.LogWarning("Closed alert {AlertId} automatically, but couldn't close all its PSA tickets. dotMARC will keep trying.", alertId);
            }
        }
    }
}
