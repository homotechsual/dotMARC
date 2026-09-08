# DMARC report insights: reason codes, auth detail, proactive IP enrichment, and alerting

## Overview

dotMARC already parses DMARC aggregate reports via DmarcRua and stores a simplified view of each
record (`ReportRecord`: source IP, message count, disposition, collapsed SPF/DKIM pass-fail,
header-from). Two categories of information the parser already has access to are discarded before
they ever reach the database:

1. **Policy override reasons** (`PolicyEvaluated.Reason[]`) - DMARC's own explanation for why a
   receiver's disposition doesn't match strict enforcement: `forwarded`, `sampled_out`,
   `trusted_forwarder`, `mailing_list`, `local_policy`, `other`. This is the single most direct
   signal for "was this reject/quarantine a benign forwarder tripping alignment, or does it look
   like a real spoofing attempt" - and today it's invisible.
2. **Per-mechanism auth detail** (`AuthResults.Dkim[]`/`AuthResults.Spf[]`) - which specific
   domain/selector was checked and its actual result (`Pass`/`Fail`/`SoftFail`/`Neutral`/
   `TempError`/`PermError`/...), collapsed today into a single Pass/Fail per record with no detail
   on which mechanism/domain produced it or whether a failure was transient (DNS hiccup) versus
   permanent (no valid record published at all).

Separately, IP enrichment (RDAP organization/country lookup via `IpInfoService.EnrichAsync`) is
currently lazy: it only runs when an admin happens to open a domain's Sources tab
(`DomainDetail.razor`). A source IP that appears in a report nobody ever views never gets looked
up, leaving gaps in exactly the ownership/location data this feature depends on.

This design closes both gaps in three phases: **Phase 1** captures the data and enriches IPs
proactively; **Phase 2** rolls the captured data up into per-domain and org-wide insight views;
**Phase 3** feeds it into alerting so a reject spike can be automatically distinguished from
benign forwarding activity.

## Phase 1: Data model

Two new child tables, both FK'd to `ReportRecord`, following the existing `TlsrptFailureDetail`
precedent (one parent row, many typed children) rather than piling nullable columns onto
`ReportRecord` itself:

```csharp
public enum DmarcAuthMechanism { Dkim, Spf }

// Mirrors the union of DmarcRua's DKIMResultType/SpfResultType (both IANA-registered, stable
// result codes). Deliberately NOT the existing AuthResult enum (Pass/Fail only) - that enum is the
// collapsed DMARC-alignment result already on ReportRecord; collapsing away TempError vs PermError
// vs Neutral here would throw away exactly the operational distinction this feature exists to
// surface ("transient DNS hiccup" vs "sender never published a valid record at all").
public enum DmarcMechanismResult { None, Default, Neutral, Pass, Fail, SoftFail, HardFail, Policy, TempError, PermError, Invalid, Unknown }

public enum DmarcPolicyOverrideType { None, Forwarded, SampledOut, TrustedForwarder, MailingList, LocalPolicy, Other }

public sealed class ReportRecordAuthDetail
{
    public int Id { get; set; }
    public int ReportRecordId { get; set; }
    public ReportRecord ReportRecord { get; set; } = null!;
    public required DmarcAuthMechanism Mechanism { get; set; }
    public required string Domain { get; set; }
    public required DmarcMechanismResult Result { get; set; }
    public string? Selector { get; set; }   // DKIM only
    public string? Scope { get; set; }      // SPF only: "MFrom" or "Helo"
    public string? HumanResult { get; set; }
}

public sealed class ReportRecordPolicyOverrideReason
{
    public int Id { get; set; }
    public int ReportRecordId { get; set; }
    public ReportRecord ReportRecord { get; set; } = null!;
    public required DmarcPolicyOverrideType Type { get; set; }
    public string? Comment { get; set; }
}
```

Both `DmarcAuthMechanism`, `DmarcMechanismResult`, and `DmarcPolicyOverrideType` use
`HasConversion<string>()` on their columns, matching the codebase's existing convention for
`DispositionResult`/`AuthResult`. New EF Core migration adds both tables (additive only, no change
to any existing table).

## Phase 1: Parsing + ingestion wiring

`ParsedReportRecord` (`src/DotMarc/Ingestion/ParsedReport.cs`) gains two new fields:

```csharp
public sealed record ParsedReportRecord(
    string SourceIp,
    int MessageCount,
    string Disposition,
    string SpfResult,
    string DkimResult,
    string HeaderFrom,
    IReadOnlyList<ParsedAuthDetail> AuthDetails,
    IReadOnlyList<ParsedPolicyOverrideReason> OverrideReasons);

public sealed record ParsedAuthDetail(DmarcAuthMechanism Mechanism, string Domain, DmarcMechanismResult Result, string? Selector, string? Scope, string? HumanResult);
public sealed record ParsedPolicyOverrideReason(DmarcPolicyOverrideType Type, string? Comment);
```

`DmarcReportParser.Parse` maps `record.AuthResults.Dkim[]`/`.Spf[]` into `ParsedAuthDetail` (DKIM
entries populate `Selector`, SPF entries populate `Scope`, the other is always null - both are
direct 1:1 field mappings already confirmed against DmarcRua 2.0.1's `DKIMAuthResultType`/
`SpfAuthResultType`) and `record.Row.PolicyEvaluated.Reason[]` into `ParsedPolicyOverrideReason`
(direct 1:1 enum mapping from `PolicyOverrideReason`).

`PollingService.StoreDmarcReportAsync`, when building each `ReportRecord` (currently around
`PollingService.cs:1262`), also populates `record.AuthDetails`/`record.OverrideReasons` from the
parsed lists. Both child collections save together with their parent in the existing single
`SaveChangesAsync` call - no extra round-trip.

## Phase 1: Proactive IP enrichment

A new leader-locked cycle, `RunIpEnrichmentCycleAsync`, follows the exact shape of the existing
`RunDnsProviderCheckCycleAsync` (own `pg_try_advisory_xact_lock` key, e.g.
`IpEnrichmentLeaderLockKey = 84_200_021`; per-item try/catch that logs and continues so one bad IP
doesn't stall the batch):

- Query the distinct `SourceIp`s across all `ReportRecord`s that either have no `IpInfo` row, or
  whose existing row satisfies `IpInfoService.NeedsLookup` (the existing 24h failure-retry window -
  reused as-is).
- Cap the batch per cycle (25 IPs) so a backlog after a bulk import doesn't turn one poll cycle
  into a long RDAP hammering session; the remainder is picked up on the next cycle.
- For each IP in the batch, call the existing `IpInfoService.EnrichAsync` unchanged - it already
  does exactly the right thing, it's just never been called from anywhere but the Sources tab's
  on-view background task.
- Wired into `ExecuteAsync` alongside the other cycles, same cadence as
  `RunDnsProviderCheckCycleAsync`.

The Sources tab's existing on-view `EnrichAsync` call stays as a fast path for a source that
hasn't hit its next scheduled cycle yet; it is no longer the only path.

## Phase 1: Sources tab drill-down UI

Follows the existing TLS-RPT tab's precedent for showing "why" detail: a plain summary column
(`string.Join(...)`), not a dialog or expandable row.

- New **"Reason"** column on the Sources table showing the distinct policy-override reason types
  seen for that source in the 30-day window (blank when never overridden, the common case).
- The existing **SPF**/**DKIM** columns gain a `MudTooltip` on hover showing the real per-mechanism
  detail for that source - which domain was checked and its actual result (`TempError`,
  `PermError`, `SoftFail`, etc., not just the collapsed Pass/Fail already shown) - placed on the
  badge that's already failing rather than a third wall-of-text column.

Since a source IP can span several `ReportRecord`s within the window, `DomainStatistics.
GetSourceAggregates` gains two new aggregated fields on `SourceAggregate`:
`IReadOnlyList<DmarcPolicyOverrideType> OverrideReasonTypes` (distinct reason types across the
source's in-window records) and `IReadOnlyList<AuthDetailSummary> AuthDetails` (distinct
`(Mechanism, Domain, Result)` tuples, deduplicated so a source failing the same way on every report
doesn't repeat itself in the tooltip). `AuthDetailSummary` is a new small record in
`DotMarc.Reporting`.

## Phase 1: Backfill for historical reports

`Report.RawXml` is already stored for every historical report, making backfill possible. Rather
than a one-off migration script, this follows the same idiom as the enrichment cycle: a new
leader-locked cycle, `RunAuthDetailBackfillCycleAsync`, picks a bounded batch of `Report` rows
where none of their `ReportRecord`s have any `ReportRecordAuthDetail` rows yet (a cheap "not yet
backfilled" marker), re-parses each one's `RawXml` via `DmarcReportParser.Parse`, and matches the
freshly-parsed records back to the already-persisted `ReportRecord`s **by ordinal position** -
both were derived from the same `feedback.Record` array in the same order (`report.Records.Add`
iterates the parsed list in order at ingestion time), so position is a safe, unambiguous key with
no fuzzy matching by SourceIp/HeaderFrom needed (which could collide if the same source appears
more than once in one report). Naturally resumable and idempotent: once every report has at least
attempted a backfill, the "not yet backfilled" query returns nothing and the cycle becomes a
permanent no-op. Malformed/no-longer-parseable `RawXml` on an old report is logged and skipped, not
fatal to the batch.

## Phase 2: Insights view

A shared pure aggregation helper, reused by both surfaces (same "pure core in `DomainStatistics`,
thin rendering in the Razor page" split already used for `GetOverallPassRate`):

```csharp
public static ReasonBreakdown GetReasonBreakdown(IEnumerable<Report> reportsInWindow)
public static ReasonBreakdown GetReasonBreakdown(IEnumerable<IEnumerable<Report>> perDomainReportsInWindow)
```

Buckets every Reject/Quarantine record's message volume into four categories (`Disposition ==
None` records are excluded - nothing to explain):

- **Benign override** - `Forwarded`/`SampledOut`/`TrustedForwarder`/`MailingList` - almost
  certainly not an attack.
- **Local policy** - the receiver's own DMARC policy enforcement - could be either.
- **Other** - the DMARC-defined catch-all.
- **No reason given** - no `<reason>` element at all - the least benign-looking signal, since a
  real receiver usually only omits `<reason>` when disposition matches assessment exactly (i.e. a
  genuine failure).

A record with multiple reason entries buckets by priority - **Benign override** wins if any entry
is `Forwarded`/`SampledOut`/`TrustedForwarder`/`MailingList` (one benign explanation is enough to
call the whole record's volume benign), else **Local policy** wins if any entry is `LocalPolicy`,
else **Other**. The second overload mirrors `GetOverallPassRate`'s existing multi-domain shape for
the org-wide view.

**Per-domain** (`DomainDetail.razor`): new `MudPaper` panel next to the existing pass/quarantine/
reject line chart, rendering the four buckets as a `MudChart` donut (matches the page's existing
`MudChart` usage, no new charting dependency) plus raw counts as text - "of 340 rejected messages:
310 benign override (forwarders/mailing lists), 12 local policy, 18 no reason given."

**Org-wide** (`Dashboard.razor`): a new panel below the existing stat-card grid, same donut + text
treatment, aggregated across every monitored domain's in-window reports via the multi-domain
overload.

## Phase 3: Alerting integration

**New alert type, `SuspiciousRejectActivity`.** Two new settings on `NotificationSettings` (same
admin-editable pattern as `MissingReportThresholdDays`/`CooldownMinutes`, exposed on
`AlertsSettings.razor`):

```csharp
public int SuspiciousRejectMinVolume { get; set; } = 10;
public int SuspiciousRejectNonBenignPercent { get; set; } = 50;
```

`AlertingService.CheckPinnedDomainsAsync`'s existing per-monitored-domain loop (already running on
`PinnedDomainHealthMonitor`'s own leader-locked cadence, so this rides the existing lock rather
than needing a new one) gains a new check per domain: compute `DomainStatistics.
GetReasonBreakdown` over its 30-day window; if total reject volume >= `SuspiciousRejectMinVolume`
and the non-benign share (Local Policy + Other + No reason given) >=
`SuspiciousRejectNonBenignPercent`, `EnsureAlertAsync(..., "SuspiciousRejectActivity", "Warning",
"Reject activity looks like more than benign forwarding", "...")`; otherwise `ResolveAlertAsync`
for that type - mirroring `MissedReport`'s existing resolve-when-healthy-again pattern exactly.
Existing alerts (`MissedReport`, `TlsrptFailure`) are untouched.

**Enrich `UnexpectedActivityOnNullRoutedDomain`.** `FlagUnexpectedActivityForNullRoutedDomainAsync`
gains a `ReasonBreakdown` parameter. `PollingService`'s call site (already sitting right where the
report was just parsed and stored, `PollingService.cs:1190`) passes the breakdown for that
specific incoming report. The alert message appends a line built from it: benign-dominant -> "This
looks like a forwarder or mailing list, not spoofing."; non-benign-dominant -> "No benign override
reason was given - this looks like a genuine spoofing attempt."

## Testing

- **Parser**: new tests asserting `AuthResults.Dkim[]/Spf[]` and `PolicyEvaluated.Reason[]` map
  correctly into `ParsedAuthDetail`/`ParsedPolicyOverrideReason`, including a multi-signature-DKIM
  fixture.
- **Ingestion**: extend the existing `StoreDmarcReportAsync` persistence tests to assert the new
  child rows land correctly (mirrors how `TlsrptFailureDetail` is already tested).
- **New cycles** (enrichment + backfill): tests mirroring `RunDnsProviderCheckCycleAsync`'s
  existing pattern - lock contention, stale-selection query, batch cap, one bad item doesn't stop
  the batch. Backfill additionally tests the ordinal-position matching against a report with
  several records, and a malformed-`RawXml` row being skipped without stopping the batch.
- **`DomainStatistics.GetReasonBreakdown`**: pure-function table tests, both overloads.
- **`AlertingService`**: threshold fire/resolve tests for `SuspiciousRejectActivity`, and a test
  asserting the null-routed-domain alert message text changes with the reason breakdown.
- No new bUnit component tests (established convention) - Sources tab tooltip/column and the two
  insight panels verified by build + manual browser check.

## Non-goals

- `ReportRecord.SpfResult`/`DkimResult`/`Disposition` stay exactly as they are (Pass/Fail/
  disposition) - the new detail is additive, not a replacement for the existing simplified fields.
- No time-series/trend view of reason mix over time - only the current 30-day window, matching the
  existing chart. A historical trend of reason mix is a future enhancement if the static breakdown
  proves useful.
- No ML/heuristic spoofing-campaign fingerprinting - Phase 3's alert is a simple volume+ratio
  threshold, nothing smarter.
- No change to TLS-RPT's existing failure-detail modeling (`TlsrptFailureDetail`) - it already
  solves the equivalent problem for TLS-RPT and is left as-is.
- No change to the Google Cloud DNS / DNS push work from earlier this session - unrelated,
  untouched.
