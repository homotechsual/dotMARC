# Manage Domains Design

## Overview

dotMARC's `Domain` rows are currently created only by auto-discovery — the
first time a DMARC aggregate report arrives for a domain (see
`docs/superpowers/specs/2026-08-09-dotmarc-design.md`, Ingestion pipeline).
This means a domain whose `rua=` DNS record is missing, wrong, or never
configured never gets a `Domain` row at all, so it can never be pinned and
can never trigger the existing "missing expected report" warning — the
system can only detect a monitored domain that *stops* reporting, not one
that never started. This design adds a "Manage domains" page that lets a
user create a `Domain` row up front, before any report has arrived for it,
closing that gap.

## Goals

- Let a user register a domain for monitoring before its first DMARC report
  arrives, so "missing expected report" detection covers domains that were
  never configured correctly, not just ones that regressed.
- Let a user remove a domain (and, if applicable, its report history)
  entirely.
- Consolidate pin/unpin alongside add/remove on one management page, while
  leaving the existing pin/unpin toggle on the Dashboard in place.
- Give the app a first, minimal navigation entry point — currently there is
  none — sufficient to reach this new page from anywhere in the app.

## Non-goals

- A general navigation menu/drawer. This design adds a single persistent
  link in the app bar, not a full nav system.
- Editing a domain's name after creation. Remove and re-add covers the rare
  typo case without adding rename semantics to a piece of data that
  `PollingService` matches on by exact string.
- Any change to how `PollingService` matches incoming reports to domains.
  It continues to look up by exact `Name` equality
  (`PollingService.cs:200`); this design only ensures domains created via
  the new page are stored in the same lowercase form real reports use, so
  they still line up.

## Route & entry point

New page at `/domains`, listing all domains. This sits alongside the
existing `/domains/{DomainName}` detail route (list vs. detail, same
resource). It gets the existing `Back` → `/dashboard` button used by
`ParseFailures.razor`, for consistency.

`MainLayout.razor` currently renders only a `MudAppBar` with static text and
no navigation of any kind. This design adds one link there — "Manage
domains", pointing at `/domains` — visible on every page. This is the
minimal fix for "no way to reach this page"; it is not a general nav menu
(see Non-goals).

## Add a domain

An inline form on the `/domains` page (text field + submit button — not a
dialog, since this is now a dedicated management page rather than a
drive-by action from the Dashboard).

On submit:
1. Trim whitespace, lowercase the input.
2. Validate: non-empty, contains at least one `.`, no internal whitespace.
   Reject with an inline error otherwise.
3. Check for an existing `Domain` with that name (case-insensitive compare,
   though input is already normalized to lowercase so this reduces to an
   exact match against stored rows, which are also always stored
   lowercase). Reject with an inline "already monitored" error if found —
   do not create a duplicate row.
4. Otherwise insert `new Domain { Name = normalized, FirstSeenUtc =
   DateTimeOffset.UtcNow, IsPinned = true }`.

Lowercasing on input matters specifically because `PollingService` matches
incoming reports to domains by exact-string equality on `Name`
(`PollingService.cs:200`), and DMARC aggregate report XML conventionally
reports the domain in lowercase. A domain added here in mixed case would
silently fail to match its first real report and `PollingService` would
create a second, separate `Domain` row for the same domain
(`PollingService.cs:203`) — normalizing on the way in is what prevents that.

A newly added domain is pinned by default (`IsPinned = true`), since the
entire purpose of adding it here is to monitor for a missing report. It
immediately shows status "Missing" on the Dashboard, via the existing logic
in `Dashboard.razor:94` (`IsPinned && LastReportReceivedUtc is null`) — no
change needed there.

## Remove & pin/unpin

The `/domains` page lists every `Domain` (name, pinned status, report
count, last report received) in a `MudTable`, same visual style as the
Dashboard's domain table.

Each row has:
- A pin/unpin toggle (`MudSwitch`), identical semantics to the existing one
  on the Dashboard (`Dashboard.razor:63`) — this becomes the second place
  it's exposed, both bound to the same `Domain.IsPinned` field.
- A delete action, opening a `MudDialog` confirmation. If the domain has one
  or more `Report` rows, the dialog states the exact count of reports (and,
  since `ReportRecord` cascades from `Report`, implicitly their records)
  that will be permanently deleted — pulled from `Domain.Reports.Count` at
  the time the dialog opens. If it has zero reports, the dialog is a plain
  "remove this domain from monitoring?" confirmation with no data-loss
  language. Confirming deletes the `Domain` row; `DotMarcDbContext.cs:28`'s
  cascade delete removes its `Report`/`ReportRecord` rows.

## Error handling

- Add: validation and duplicate-name failures are shown inline next to the
  form field, not as a page-level error — the user can immediately correct
  and resubmit.
- Remove: if the delete fails at the database level (unexpected — no known
  case in the current schema), show a `MudSnackbar` error and leave the row
  in place; do not optimistically remove it from the displayed list before
  the delete succeeds.

## Testing

Following the existing test suite's `Testcontainers.PostgreSql` pattern:

- Adding a domain creates exactly one `Domain` row, pinned, with no reports,
  and the stored `Name` is lowercase regardless of input casing.
- Adding a domain that already exists (any input casing) is rejected and no
  second row is created.
- A domain added through this page is correctly matched (not duplicated) by
  `PollingService` when its first real report arrives — this is the
  regression test for the bug this design fixes.
- Deleting a domain with existing reports cascades: `Report` and
  `ReportRecord` rows for that domain are gone afterward.
- The delete confirmation dialog shows the correct report count for a
  domain with history, and the no-data-loss variant for one without.
