# DMARC DNS Status Design

## Overview

dotMARC tells you whether a domain's aggregate reports are arriving, but not whether the DNS
records that make that possible are actually correct. A domain can look fine (reports flowing,
pass rate healthy) while quietly relying on records that happen to work today and could break
silently — and a domain that's never sent a report gives no signal at all about *why*: no DMARC
record published, a `rua=` tag pointing at the wrong mailbox, or (the case this design's title
names directly) a missing external-reporting authorization record. This design adds a DNS-based
check, queried against Cloudflare's public resolvers, that answers that question directly and
surfaces it as a new tracked status per domain.

Bundled into this same design, at the user's request ("whilst we're in here"): renaming
`Domain.IsPinned` to `IsMonitored`, since the field's own existing doc comment already describes
it that way ("explicitly monitored vs. auto-discovered") and "Pinned" was always an
implementation-detail name borrowed from a UI pattern, not a description of what the field means.

## Goals

- For every domain, know whether its own `_dmarc.<domain>` TXT record exists, is well-formed, and
  actually authorizes reports to be sent to dotMARC's configured mailbox.
- Know whether the RFC 7489 §7.1 external-reporting authorization record — published at
  `<domain>._report._dmarc.<mailbox-domain>`, e.g. `blossom.wales._report._dmarc.mjco.uk` — exists
  when it's required (i.e. whenever the mailbox's domain differs from the monitored domain, which
  is effectively always in this app's shared-mailbox architecture).
- Query Cloudflare specifically (not whatever resolver the host happens to have configured), so
  results are consistent and independent of the runtime environment's own DNS configuration.
- Keep this fresh automatically, without requiring a user to remember to check.
- Show a compact, at-a-glance summary on the Dashboard, with the full reason on the per-domain
  drilldown page — and nothing at all on Manage Domains, which is a configuration surface only.
- Rename `IsPinned` → `IsMonitored` throughout (data model, service, both Razor pages, tests).

## Non-goals

- Checking anything beyond the two records above (e.g. SPF, DKIM, MX — this is a DMARC-record
  status check, not a general domain-health scanner).
- A manual "check now" trigger. Automatic, on the same cadence as polling, is the only trigger —
  see [Trigger and cadence](#trigger-and-cadence) below.
- Querying any DNS provider other than Cloudflare, or falling back to a second provider if
  Cloudflare is unreachable — a failed check just leaves the domain's status as whatever it was
  before (see [Failure handling](#failure-handling)).
- Anything on Manage Domains. That page manages configuration (add, remove, monitored toggle,
  order); it doesn't show status of any kind, DNS or otherwise — this design doesn't touch it
  beyond the terminology rename.

## Data model

`Domain` gains three fields:

- **`DmarcCheckStatus`** (enum: `NotChecked`, `Ok`, `MissingOwnRecord`, `Misconfigured`,
  `MissingAuthorizationRecord`) — the single terminal status from the waterfall below. Default
  `NotChecked` for a domain that hasn't been checked yet (including every domain that exists
  before this migration runs).
- **`DmarcCheckedUtc`** (`DateTimeOffset?`) — when the check last ran. `null` until the first
  check.
- **`DmarcCheckDetail`** (`string?`) — a human-readable reason, e.g. `"rua= points to
  other@example.com, not rua.dmarc@mjco.uk"` or `"No TXT record found at
  blossom.wales._report._dmarc.mjco.uk"`. `null` when `DmarcCheckStatus` is `Ok` or `NotChecked`.

Same migration renames `IsPinned` → `IsMonitored` (a plain column rename, not a new column — see
[Terminology rename](#terminology-rename-ispinned--ismonitored)).

## The check: a waterfall, not two independent lookups

Each step only runs if the previous one passed, so a domain with no DMARC record at all costs one
DNS query, not two:

1. Query `_dmarc.<domain>` as TXT. No record (NXDOMAIN) → `MissingOwnRecord`, stop.
2. Record exists. If it doesn't start with `v=DMARC1`, or its `rua=` tag (comma-separated list of
   `mailto:` URIs per RFC 7489) doesn't include the configured mailbox address
   (`GraphOptions.MailboxAddress`, case-insensitive) → `Misconfigured`, stop.
3. Record is valid and correctly addressed. If the mailbox's domain (the part after `@` in
   `MailboxAddress`) equals the monitored domain → `Ok`, stop — same-domain destinations are
   exempt from the authorization-record requirement.
4. Domains differ. Query `<domain>._report._dmarc.<mailbox-domain>` as TXT. No record →
   `MissingAuthorizationRecord`. Record present → `Ok`. (Per RFC 7489, this record should itself
   start with `v=DMARC1`, but real-world publishers are inconsistent about this in practice for a
   record whose only job is presence-as-consent — its mere existence is treated as sufficient
   here, matching what recipients' own DMARC implementations generally accept.)

## Querying Cloudflare

Cloudflare's DNS-over-HTTPS JSON API, not raw UDP to 1.1.1.1/1.0.0.1 — this runs over HTTPS (443),
which is reliably open outbound from Azure Container Apps, unlike raw UDP:53, and needs no new DNS
protocol library:

```
GET https://cloudflare-dns.com/dns-query?name=<name>&type=TXT
Accept: application/dns-json
```

Response shape (relevant fields only):

```json
{ "Status": 0, "Answer": [ { "type": 16, "data": "\"v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk\"" } ] }
```

`Status: 0` is NOERROR; `Status: 3` is NXDOMAIN (the "no record" case in the waterfall above). A
TXT record's `data` field carries the literal record text still wrapped in double quotes (and, for
records over 255 bytes, as multiple adjacent quoted segments that concatenate into the full
value) — both need stripping/joining before parsing `v=`/`rua=` out of it.

New service, following this project's existing `IGraphMailboxClient`/`GraphMailboxClient`
pattern exactly (interface + typed `HttpClient` implementation, registered the same way in
`Program.cs`):

```csharp
public interface IDmarcDnsChecker
{
    Task<DmarcCheckResult> CheckAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken);
}
```

`DmarcDnsChecker` implements the waterfall above using this client, registered as
`AddHttpClient<IDmarcDnsChecker, DmarcDnsChecker>(client => client.BaseAddress = new
Uri("https://cloudflare-dns.com/"));`.

## Trigger and cadence

Runs inside `PollingService`'s existing cycle (the same background service, same leader-election
advisory lock — no second scheduled job to build or coordinate), after the mailbox poll itself.
For every `Domain` row (not only monitored ones — an auto-discovered domain benefits from knowing
its authorization record is actually in place just as much as a manually-added one), skip unless
`DmarcCheckedUtc` is `null` or more than 24 hours old. On a normal 5-minute poll interval this
means the DNS check work only actually happens roughly once a day per domain, not every cycle.

## Failure handling

If the Cloudflare request itself fails (network error, non-2xx response, timeout) for a given
domain's check, that domain's `DmarcCheckStatus`/`DmarcCheckedUtc`/`DmarcCheckDetail` are left
unchanged — no partial update, no "unknown/error" status distinct from `NotChecked`. The domain is
simply retried on the next cycle once 24 hours have passed since its last successful check (or
immediately, since `DmarcCheckedUtc` never advanced). This mirrors `PollingService`'s existing
policy of leaving a message unread and retrying rather than recording a failure state that has to
be separately cleared.

## Dashboard changes

`Dashboard.razor`'s domain table:

- Existing `Status` column (report pass-rate health: OK/Warning/Missing) is relabeled
  **`Report Status`** — disambiguating it from the new column, not a behavior change.
- New **`DNS Status`** column: a chip in the same visual style as `Report Status`, driven directly
  by `DmarcCheckStatus` — `Ok` (green), `MissingOwnRecord`/`Misconfigured` (red),
  `MissingAuthorizationRecord` (amber/warning), `NotChecked` (neutral grey, "Not checked yet").
- Existing `Pinned` column is relabeled **`Monitored`** (see rename section) — no behavior change,
  same toggle switch.

## Domain detail changes

`DomainDetail.razor`'s Overview tab gains a "DMARC record status" panel (same kind of addition as
the Dashboard's existing "Polling status" panel from a prior design): the full status, the
`DmarcCheckedUtc` timestamp, and — when status isn't `Ok`/`NotChecked` — the specific
`DmarcCheckDetail` reason text.

## Terminology rename: `IsPinned` → `IsMonitored`

Mechanical rename, not a behavior change. Touches:

- `Domain.IsPinned` → `Domain.IsMonitored` (plus the migration's `RenameColumn`).
- `DomainManagementService.SetPinnedAsync` → `SetMonitoredAsync` (parameter `isPinned` →
  `isMonitored`, doc comments referencing "pin"/"pinned" updated to "monitor"/"monitored").
- `Dashboard.razor`: `context.IsPinned` → `context.IsMonitored`, `TogglePinnedAsync` →
  `ToggleMonitoredAsync`, the `DomainRow` record's field, column header text.
- `ManageDomains.razor`: same field/call-site renames, column header text ("Pinned" → "Monitored").
- `PollingServiceTests.cs`, `DomainManagementServiceTests.cs`, `DotMarcDbContextTests.cs`: every
  reference to `IsPinned`/`SetPinnedAsync` in setup or assertions.

Not touched: the original project design spec
(`docs/superpowers/specs/2026-08-09-dotmarc-design.md`) and every prior migration file — both are
historical records of what was true when they were written, the same reason a database migration
is never hand-edited after the fact. The new migration this design adds is the record of the
rename; the old ones stay exactly as they are.

## Testing

Following the existing test suite's `Testcontainers.PostgreSql` pattern for anything touching the
database, and a fake/mock `IDmarcDnsChecker` (or a fake `HttpMessageHandler`, matching
`FakeHttpMessageHandler`'s existing use for Graph client tests) for anything touching the waterfall
logic itself, so no test makes a real network call:

- Each waterfall branch (no record → `MissingOwnRecord`; record present but wrong/missing `rua=`
  → `Misconfigured`; valid record, same domain as mailbox → `Ok` with no second query made; valid
  record, different domain, authorization record present → `Ok`; valid record, different domain,
  authorization record absent → `MissingAuthorizationRecord`) gets its own test against a fake
  Cloudflare response.
- TXT record value parsing handles the quoted/multi-segment `data` field shape, and a `rua=` tag
  with multiple comma-separated addresses where only one matches.
- `PollingService`'s integration: a domain with `DmarcCheckedUtc` more than 24h old gets
  re-checked in a cycle; one checked 1 hour ago doesn't; a domain with `DmarcCheckedUtc = null`
  (including one freshly created this cycle) gets checked.
- A failed Cloudflare call (simulated via the fake handler returning a non-success response)
  leaves `DmarcCheckStatus`/`DmarcCheckedUtc`/`DmarcCheckDetail` unchanged from whatever they were
  before the cycle ran.
- No automated test for the Dashboard/DomainDetail UI changes themselves, consistent with this
  project's established precedent (no Blazor component-rendering test framework).
