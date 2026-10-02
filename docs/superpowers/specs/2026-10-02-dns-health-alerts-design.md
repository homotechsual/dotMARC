# DNS health alerts: alert when a domain's DNS health breaks, its DMARC policy weakens or its nameservers change

Date: 2026-10-02. Status: approved design, not yet planned or built. Target release: v0.8.0.

## Problem

dotMARC checks every domain's DMARC, DMARC authorization, TLS-RPT, SPF, MX, DKIM and MTA-STS records about once a
day and stores each result on the domain. Nothing compares a result with the last one, so a check that breaks (an SPF
record deleted during a DNS migration, the DMARC authorization record removed) only shows up if someone opens that
domain. Usually it's noticed days later, when reports stop arriving. This is the "Widen alerting beyond
MissedReport / TLSRPTFailure" idea on the roadmap.

## Decisions

1. **Nine new alert types**: one per health check (seven), plus **DMARC policy weakened** and **Nameservers
   changed**. MTA-STS hosting failing is the MTA-STS check's alert.
2. **Each check is set to Off, When it breaks, or Whenever it fails.** *When it breaks* alerts only once the check
   has passed at some point. All seven default to *When it breaks*, so half-configured domains and upgrades are quiet.
3. **A failure is confirmed before it alerts**: the check is re-run about 15 minutes later, and the alert is raised
   only if it still fails.
4. **Alerts go through the existing path** (`AlertingService.EnsureAlertAsync`): the same cooldown, Teams or generic
   webhook delivery, HaloPSA tickets and per-type ticket rules as today's alerts.
5. **Check alerts close themselves** when the check passes again, when it becomes not applicable, or when its
   setting is turned off.
6. **Policy and nameserver alerts compare against a baseline**, because the change may be deliberate. They close
   themselves if the value goes back. Otherwise they close when someone acknowledges them, when their Halo ticket is
   closed, or after an optional number of days. Any of those accepts the current value as the new baseline.
7. **A separate, pure evaluator** decides what to do, using a new state table. The checks themselves barely change.

## Alert types

Keys are stored on alerts and ticket rules, so they must never change once shipped. All are added to
`AlertTypes.All`.

| Key | Display name | Severity | Ticket by default |
| --- | --- | --- | --- |
| `DmarcRecordBroken` | DMARC record broken | Warning | Yes |
| `DmarcAuthorizationBroken` | DMARC authorization record broken | Warning | Yes |
| `TlsrptRecordBroken` | TLS-RPT record broken | Warning | Yes |
| `SpfRecordBroken` | SPF record broken | Warning | Yes |
| `MxRecordBroken` | MX broken | Warning | Yes |
| `DkimRecordBroken` | DKIM broken | Warning | Yes |
| `MtaStsFailing` | MTA-STS failing | Warning | Yes |
| `DmarcPolicyWeakened` | DMARC policy weakened | Warning | Yes |
| `NameserversChanged` | Nameservers changed | Info | No |

### What counts as passing, failing or ignored

| Check | Passing | Failing | Ignored |
| --- | --- | --- | --- |
| DMARC | `Ok` | `MissingOwnRecord`, `Misconfigured`, `MissingAuthorizationRecord` | `NotChecked` |
| DMARC authorization | `Ok` | `Missing` | `NotChecked`, `NotApplicable` |
| TLS-RPT | `Ok` | `MissingOwnRecord`, `Misconfigured` | `NotChecked` |
| SPF | `Ok`, `NullSpf` | `MissingRecord`, `MultipleRecords`, `Misconfigured` | `NotChecked` |
| MX | `Ok`, `NullMx` | `MissingRecord`, `UnresolvableTarget` | `NotChecked` |
| DKIM | `Ok` | `Missing`, `Misconfigured` | `NotConfigured` (no selectors) |
| MTA-STS | `Active` | `Failed` | MTA-STS turned off, `NotConfigured`, `PendingDns`, `PendingCertificate` |

Only monitored domains (`IsMonitored`) are evaluated, as for the missed-report alert. A domain that stops being
monitored has its open alerts of these types resolved.

## Settings

New fields on `NotificationSettings`, shown in a new **DNS health alerts** panel on Alerts › Settings and saved
(and audited) with the rest of the settings:

- One mode per check: `Off`, `WhenItBreaks` (default) or `WheneverItFails`. Stored as a string-backed enum per check.
- `DmarcPolicyWeakenedEnabled` (default on) and `NameserversChangedEnabled` (default on).
- `AcknowledgeableAutoCloseDays` (default 0, meaning never). When above 0, an open DMARC policy weakened or
  nameservers changed alert older than this is closed automatically, as if acknowledged.

The global **Alerts enabled** switch still turns all of this off.

## State

A new table, `DomainAlertStates`, with one row per domain per watched item:

| Column | Meaning |
| --- | --- |
| `DomainId` | The domain. Cascade-deleted with it. |
| `Item` | `Dmarc`, `DmarcAuthorization`, `Tlsrpt`, `Spf`, `Mx`, `Dkim`, `MtaSts`, `DmarcPolicy` or `Nameservers` (string). |
| `HasPassed` | The check has passed at least once since dotMARC started watching it. |
| `Baseline` | The accepted DMARC policy (for example `p=reject; sp=reject; pct=100`) or the accepted nameservers (sorted, `;`-joined). Null for checks. |
| `PendingSinceUtc` | When the current unconfirmed failure or change was first seen. Null if there isn't one. |
| `RecheckDueUtc` | When the confirmation recheck becomes due (`PendingSinceUtc` + 15 minutes). |

There's a unique index on (`DomainId`, `Item`). Rows are created by the evaluator the first time it sees a domain.

## DMARC policy

The DMARC check already fetches the record. It also parses three tags into new domain fields:

- `DmarcPolicy`: `p`, as `None`, `Quarantine` or `Reject`. Null if there's no record or `p` is missing or invalid.
- `DmarcSubdomainPolicy`: `sp`. If missing, it is the same as `p`.
- `DmarcPercent`: `pct`, from 0 to 100. If missing or invalid, it is 100.

Tag names and values ignore case and surrounding spaces.

A policy is **weaker** than the baseline if any of `p`, `sp` or `pct` is lower (none < quarantine < reject). It is
**at least as strong** if none is lower. With no record (null `DmarcPolicy`), the policy isn't compared; the DMARC
check's own alert covers a missing record.

## The evaluator

`DnsHealthAlertEvaluator` is a pure function. It takes a domain's stored check results, policy and nameservers, its
state rows, the settings and the current time. It returns the updated state rows and a list of actions: raise an
alert (with type, title and message), or resolve an alert.

`AlertingService.CheckPinnedDomainsAsync` runs it for each monitored domain on every monitor cycle (every 5 minutes
by default), saves the state, and carries out the actions with the existing `EnsureAlertAsync` and
`ResolveAlertAsync`.

### For each of the seven checks

- **Passing:** set `HasPassed`, clear the pending failure, and resolve the check's alert.
- **Ignored, or the check's mode is Off:** clear the pending failure and resolve the check's alert.
- **Failing in *When it breaks* mode, never passed:** clear any pending failure and do nothing else.
- **Failing, nothing pending:** set `PendingSinceUtc` to now and `RecheckDueUtc` to now + 15 minutes. Don't alert.
- **Failing, pending, and the check ran again at or after `RecheckDueUtc`** (its `...CheckedUtc` is at or after it):
  raise the alert. The pending failure is left in place until the check passes, is ignored or is turned off, so the
  alert keeps being raised each cycle, and `EnsureAlertAsync`'s existing de-duplication and cooldown decide whether
  anything is sent.
- **Failing, pending, recheck not yet run:** do nothing.

Alert messages name the domain, the check, the status and the stored check detail, for example "The SPF record for
contoso.com is missing (No TXT record found at contoso.com). It was passing before."

### DMARC policy weakened

- **Setting off:** clear any pending change and resolve the alert. The baseline is kept.
- **No record:** clear any pending change. Don't resolve an open alert: the record's absence is the DMARC check's
  alert, and the policy alert stays until the record is back.
- **No baseline yet:** the current policy becomes the baseline.
- **At least as strong as the baseline:** the baseline becomes the current policy, the pending change is cleared,
  and the alert is resolved.
- **Weaker:** confirmed the same way as a check (pending, then the DMARC check has run again at or after
  `RecheckDueUtc`), then raised, with a message such as "contoso.com's DMARC policy went from p=reject; pct=100 to
  p=quarantine; pct=50."

### Nameservers changed

- **Setting off:** clear any pending change and resolve the alert. The baseline is kept.
- **None detected:** clear any pending change. Leave everything else alone.
- **No baseline yet:** the current set becomes the baseline.
- **Same set as the baseline** (ignoring order and case): clear the pending change and resolve the alert.
- **Different set:** confirmed the same way (the DNS provider check has run again at or after `RecheckDueUtc`),
  then raised with severity Info, naming the old and new nameservers and the detected provider.

## The confirmation recheck

Each polling check cycle currently re-runs domains whose `...CheckedUtc` is more than 24 hours old. It will also
re-run a domain whose state row for that check (or, for the policy, the DMARC check; for nameservers, the DNS
provider check) has `RecheckDueUtc` at or before now while the check's `...CheckedUtc` is still before
`RecheckDueUtc`. Polling runs every 5 minutes, so a recheck happens within about 20 minutes of the first failure.
The domain page's Recheck button counts as a recheck too.

If polling isn't running, no recheck happens, and nothing alerts. That fails safe.

## Closing policy and nameserver alerts

These alerts are **acknowledgeable**. Closing one in any of these ways resolves it, closes its Halo ticket (if it
has one and the alert wasn't closed by that ticket), and sets the baseline to the current value:

- **Acknowledge**, a button on the Alerts page for open acknowledgeable alerts, shown with the `AlertsManage`
  permission. A confirmation says the current policy or nameservers will become the accepted ones. It's recorded in
  the audit log as `alert.acknowledged` ("Alert acknowledged").
- **Closing the Halo ticket.** The webhook already resolves the alert; it now also updates the baseline.
- **Automatic close**, when `AcknowledgeableAutoCloseDays` is above 0, run by the monitor. Recorded in the audit log
  as `alert.acknowledged` by the system.

Check alerts aren't acknowledgeable: they close when the check passes. Closing a check alert's Halo ticket resolves
the alert as it does today. If the check is still failing, it's raised again (with a new ticket) on the next monitor
cycle, the same as a missed-report alert whose ticket is closed while reports are still missing: the cooldown only
holds back repeats while an alert is open.

## Upgrading

No state rows exist when this ships. On the first cycle each domain gets its rows: passing checks are marked as
having passed, and the current policy and nameservers become the baselines. Nothing is raised, except checks set to
*Whenever it fails*, which are confirmed and raised as usual. All checks default to *When it breaks*, so upgrading
is silent.

## Demo data

The demo dataset gets a few open alerts of the new types: an SPF record broken, a DMARC policy weakened and a
nameservers changed alert, with matching state rows, so the Alerts page shows them and Acknowledge can be tried.

## Docs

- `website/docs/alerts.mdx` gets a section on DNS health alerts: the nine types, the per-check modes, confirmation,
  baselines, and the three ways to close a policy or nameserver alert.
- The roadmap idea "Widen alerting beyond MissedReport / TLSRPTFailure" is marked complete.

## Testing

- **Evaluator** (pure, no database): every status of every check in each mode; never-passed versus passed;
  confirmation timing, including a recheck that hasn't run yet and a manual recheck; Off and ignored statuses
  resolving; a monitored domain becoming unmonitored; policy comparisons on `p`, `sp` and `pct`, including their
  defaults and an absent record; nameserver set changes ignoring order and case; first-run behaviour.
- **Policy parsing**: missing tags, spaces, mixed case, invalid values and `pct` out of range.
- **Database tests**:
  - each polling check cycle re-runs a domain with a due recheck, and not one that's already been rechecked;
  - a failure raises one alert after its recheck and resolves when the check passes;
  - Acknowledge resolves the alert, closes the ticket, moves the baseline and writes the audit entry;
  - a Halo ticket close moves the baseline;
  - automatic close;
  - the first run on existing domains raises nothing.
- **Coverage**: the existing reflection test that every `AuditActions` constant is in `All` covers the new action;
  every new alert type is in `AlertTypes.All`.
- **Demo check in the browser**: the settings panel, the new alerts listed, and Acknowledge.

## Rollout

One migration adds the settings fields, the domain policy fields and the `DomainAlertStates` table. Defaults keep
upgrades silent. Ships in v0.8.0.

## Out of scope

- An indicator on the domain page for "failing, confirming" or each check's alert mode.
- A history of check results over time.
- Acknowledging check alerts.
- Alerts for other DMARC tag changes (`rua`, `adkim`, `aspf`).
