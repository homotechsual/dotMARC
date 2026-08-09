# dotMARC Design

## Overview

dotMARC is a self-hosted DMARC aggregate report analyzer for monitoring email
authentication posture across multiple client domains from a single MSP
tenant. All monitored domains publish `rua=mailto:` pointing at one shared
mailbox in dotMARC's own tenant; dotMARC polls that mailbox via Microsoft
Graph, parses incoming DMARC aggregate reports, and presents pass/fail trends
and sending-source breakdowns per domain through a Blazor dashboard.

## Goals

- Give MSP staff a single dashboard to see, at a glance, which client domains
  have healthy DMARC posture and which need attention.
- Turn raw DMARC aggregate report XML (one file per reporting organization
  per domain per day, typically) into per-domain trends and per-source
  breakdowns, without needing to open individual report attachments.
- Flag domains that stop sending expected reports (a signal that DNS or the
  domain's DMARC record broke), for domains explicitly pinned as monitored.
- Run as a single, simply-deployed Docker container.

## Non-goals (v1)

- **Forensic (RUF) report support.** Most major receivers (Google, Microsoft,
  Yahoo) no longer send these for privacy reasons; building for them is
  largely wasted effort. `DmarcRua`, the parsing library this project depends
  on, doesn't support them either — a clean alignment, not a limitation to
  work around.
- **Push notifications.** No email digest, no real-time alerting in v1. The
  dashboard is the only surface for now; both are natural fast-follows once
  real report data shows what's actually worth alerting on.
- **The 12-month rollup/purge job.** The data model is designed to support
  aggregating and purging report data older than 12 months (see Retention
  below), but the job itself isn't built in v1 — a fresh deployment won't
  have any data old enough to need it for a year.
- **Multi-tenant Graph auth.** Every monitored domain's reports land in one
  mailbox in dotMARC's own tenant (via each domain's own `rua=` DNS record),
  so there is exactly one Graph app registration and one set of credentials
  to manage — not one per client domain.

## Architecture

Single Docker container, three logical pieces sharing one ASP.NET Core
process:

- **`PollingService`** (`BackgroundService`) — polls the shared mailbox via
  Microsoft Graph on a configurable interval, fetches unread messages,
  extracts DMARC report attachments, decompresses them (gzip or zip,
  depending on the sending provider), hands the resulting XML to `DmarcRua`
  for parsing, stores the parsed records, and marks the message read.
- **Blazor Server + MudBlazor** — the dashboard UI, served from the same
  process. Blazor Server (not WebAssembly) was chosen because this is an
  internal tool behind Entra auth with no offline requirement, and it gets
  live updates (e.g. "new report just parsed" appearing without a manual
  refresh) for free via its existing SignalR connection.
- **SQLite via EF Core** — a single database file on a mounted volume, holding
  domains, reports, per-source records, and parse failures. Chosen over
  PostgreSQL for deployment simplicity: one container, one file to back up,
  and the data volume here (aggregate reports for a modest number of client
  domains) doesn't approach the scale where SQLite becomes a constraint.

## Data model

- **`Domain`** — domain name (unique), `IsPinned` (bool — explicitly
  monitored vs. auto-discovered from incoming reports), first-seen and
  last-report-received timestamps.
- **`Report`** — one row per aggregate report received: reporting
  organization name, report ID, date range covered, `Domain` foreign key, the
  raw decompressed XML (kept for the 12-month retention window so a report
  can be re-inspected or re-parsed if the parsing logic improves), received
  timestamp.
- **`ReportRecord`** — one row per sending source within a report: source IP,
  message volume, disposition (none / quarantine / reject), SPF result,
  DKIM result, header-from domain. This is what per-domain source breakdowns
  and trend charts query against — it's the granular data, `Report` is the
  envelope it arrived in. (A resolved hostname per source IP would make the
  Sources tab more readable — e.g. "mail.contoso.io" instead of a bare IP —
  but that needs its own decision on how resolution happens and wasn't part
  of this design pass; worth raising as a fast-follow, not assumed here.)
- **`ParseFailure`** — Graph message ID, failure reason, timestamp. Recorded
  whenever a message can't be turned into a `Report` (see Ingestion below).

`Domain.IsPinned` is how auto-discovery and explicit monitoring coexist: any
domain seen in an incoming report is automatically added to the dashboard
with `IsPinned = false`; pinning a domain (via the dashboard) doesn't change
how its reports are processed, it only makes "no report received recently"
worth surfacing as a warning for that specific domain, rather than being
indistinguishable from a domain nobody expects reports from.

## Ingestion pipeline

Per poll cycle: query the mailbox for unread messages → for each, extract any
DMARC report attachment → detect and decompress (gzip or zip) → pass the
decompressed XML stream to `DmarcRua` → map the parsed result into `Report`
and `ReportRecord` rows, creating the `Domain` row if this is the first report
seen for it → mark the message read.

**Failure handling:** if any step in that chain fails — corrupt attachment,
unexpected format, a message that isn't a DMARC report at all — the failure
is logged, a `ParseFailure` row is recorded, and **the message is left
unread**. This means a failure is retried on the next poll (useful if the
cause was transient, or if a later dotMARC release fixes a parsing gap) and
is never silently discarded — it surfaces on the dashboard as "N unparseable
messages" rather than only existing in a log nobody's watching. (This
project's own on-call agent build hit exactly that failure mode — an
exception caught and only logged, invisible in a console-free deployment —
twice. Not repeating it here.)

## Dashboard

**Landing page** — a global summary strip (four tiles: domains monitored,
overall pass rate, domains with warnings, domains missing an expected
report) above a sortable domain health table (domain, status badge, pass
rate, last report received, 30-day trend sparkline). Rows click through to
the per-domain detail page. Validated via mockup: table-first was chosen
over a card-grid or global-trend-first layout because the MSP use case is
"which specific client needs attention," which a scannable list answers
faster than a big aggregate chart.

**Per-domain detail page** — tabbed: **Overview** (pass/fail/quarantine trend
chart, summary stats), **Sources** (breakdown table: source, volume, SPF,
DKIM, disposition), **Raw reports** (list of individual reports received,
reporting org, date range). Tabbed was chosen over a single scrolling page to
give the trend chart room to breathe and keep each tab focused.

## Auth & security

- **Dashboard access:** Entra ID sign-in (OpenID Connect) against the same
  tenant the Graph app registration lives in. Staff sign in with their normal
  M365 account; access can be restricted by Entra group.
- **Mailbox access:** the Graph app registration is granted application-level
  `Mail.Read`, then scoped down via an Exchange **Application Access
  Policy** to just the DMARC reports mailbox — so even though `Mail.Read` is
  tenant-wide by default, this app registration technically cannot read any
  other mailbox in the tenant. One-time Exchange PowerShell setup step,
  meaningfully better blast radius if credentials ever leak.

## Dependencies

- **`DmarcRua`** (NuGet, MIT, zero production dependencies, targets
  `netstandard2.0`) — parses DMARC aggregate report XML (v1 and v2) into
  typed objects. dotMARC still owns fetching the message and decompressing
  the attachment; `DmarcRua` owns the XML schema itself, including whatever
  per-provider formatting quirks it's already had to handle.
- **MudBlazor** — component library for the dashboard (tables, cards,
  navigation, charts, theming — including dark mode, which a hand-rolled
  mockup during this project's design phase visibly got wrong).
- **Microsoft Graph SDK + MSAL.NET** (client-credentials/app-only flow) — for
  polling the mailbox. App-only auth here is simpler than the interactive
  broker flow this project's sibling (the on-call agent) needed, since
  there's no interactive user to sign in.

## Retention

Raw reports (`Report.RawXml`) are kept in full for a rolling 12 months. Past
that window, the design intends for reports to be aggregated into permanent
summary statistics and the raw XML purged — but per the Non-goals section,
that rollup job is not built in v1, since no deployment will have data old
enough to need it for a year. The data model doesn't need to change to add
it later.

## Deployment

Single Dockerfile (standard ASP.NET Core container pattern), SQLite database
file on a mounted volume, configuration via environment variables
(`Graph__ClientId`, `Graph__TenantId`, `Graph__ClientSecret`, the monitored
mailbox address, poll interval) — the same env-var/`appsettings.json`
configuration convention already established by this project's sibling,
oncall-busybar-agent.
