# Poll Status Design

## Overview

`PollingService` runs every `Graph__PollIntervalSeconds` (default 300s) but currently leaves no
trace of its own activity anywhere a user can see: no record of when it last ran, how many
messages it looked at, how many turned into reports, or whether the last cycle even succeeded.
When something's wrong with polling (Graph credentials expired, the mailbox app registration lost
access, an unhandled exception in the fetch itself), the only symptom today is silence — no new
data on the Dashboard, no signal that polling itself is the problem rather than there simply being
nothing to report. This design adds a small operational status panel to the Dashboard backed by a
new persisted record of each poll cycle's outcome.

## Goals

- Show, on the Dashboard, when the mailbox was last polled (ISO 8601), how many messages that
  cycle checked, how many were successfully parsed into reports, how many failed to parse, and
  whether the cycle itself succeeded or errored.
- Persist this to the database so it survives restarts/redeploys — this app redeploys often enough
  that in-memory-only status would be misleading right after almost every deploy.
- Keep a short raw history (7 days) rather than only the single latest cycle, so an intermittent or
  recently-resolved polling problem is diagnosable after the fact, not just visible in the instant
  it's happening.
- Roll 7-day-old raw history into a permanent daily summary rather than discarding it outright, so
  long-term polling health isn't lost to the retention window.

## Non-goals

- A UI view of the history or daily-summary data. This design captures both, but only surfaces the
  single latest cycle on the Dashboard — a "polling health over time" view is a natural fast-follow
  once there's actually a few weeks of summary data to look at, not something to build against zero
  real data now.
- Any change to what counts as a parse failure, or to `ParseFailure`'s own per-message retry
  behavior (`PollingService.RecordParseFailureAsync`). This only adds cycle-level counting around
  the existing per-message logic, it doesn't change it.
- Alerting (email/webhook) on a failed or overdue poll cycle. The Dashboard panel is the only
  surface, matching this project's existing "dashboard only, no push notifications in v1" stance
  from the original design spec.

## Data model

Two new entities, alongside `Domain`/`Report`/`ReportRecord`/`ParseFailure` in `src/DotMarc/Data/`:

- **`PollCycle`** — one row per poll cycle that actually ran (a cycle skipped because another
  replica held the leader lock writes nothing — "last polled" should reflect when polling actually
  happened). Fields: `Id`, `PolledUtc` (`DateTimeOffset`, when the cycle finished), `MessagesChecked`
  (int — total unread messages the cycle fetched, attachments or not), `ReportsParsed` (int — messages
  that were processed without throwing, whether that produced a genuinely new `Report` row or matched
  an already-stored duplicate; this mirrors what "processing succeeded" means to `PollOnceAsync`
  today, not a distinction the feature needs to add), `ParseFailures` (int — messages that threw and
  were recorded as a `ParseFailure` this cycle), `Succeeded` (bool), `ErrorMessage` (`string?` —
  populated only when `Succeeded` is false, i.e. the cycle itself threw outside the existing
  per-message try/catch — for example the initial `GetUnreadMessagesAsync` call failing).
- **`PollCycleDailySummary`** — one row per UTC calendar day, created/updated only when that day's
  raw `PollCycle` rows are rolled up (see Retention below). Fields: `Date` (`DateOnly`, unique),
  `TotalCycles`, `SuccessfulCycles`, `FailedCycles`, `TotalMessagesChecked`, `TotalReportsParsed`,
  `TotalParseFailures` (all int).

Requires one new EF Core migration, applied automatically at startup via the existing
`DatabaseMigrator.MigrateWithLeaderLockAsync` — no manual step, same as every prior schema change.

## `PollingService` changes

- The private `PollOnceAsync(IGraphMailboxClient, DotMarcDbContext, CancellationToken)` loop
  (`PollingService.cs`) changes from `Task` to returning a small internal result — messages
  checked, reports parsed, parse failures — accumulated as it iterates, rather than doing nothing
  with those counts as it does today.
- `RunPollCycleAsync`, after successfully acquiring the leader lock, calls `PollOnceAsync` and
  writes one `PollCycle` row from the result (`Succeeded = true`, `ErrorMessage = null`). If
  `PollOnceAsync` itself throws (not a per-message failure — those are already caught internally —
  but something failing before/outside that loop, e.g. the mailbox fetch), the exception is caught,
  a `PollCycle` row is written with `Succeeded = false` and `ErrorMessage = ex.Message`, and the
  exception is then rethrown so `ExecuteAsync`'s existing `LogWarning` on cycle failure still fires
  unchanged — this only adds a DB record alongside the existing log line, it doesn't change what
  gets logged or how retries work.
- Immediately after writing the new row, a prune-and-rollup step runs: find any `PollCycle` rows
  whose `PolledUtc` falls on a UTC calendar day that is now more than 7 days in the past, group them
  by that date, upsert each date's aggregate counts into `PollCycleDailySummary`, then delete those
  raw rows. Anchoring the cutoff to a calendar-day boundary (not a rolling timestamp) means a given
  day is only ever rolled up once — by the time any of its rows are eligible, all of that day's rows
  are already closed out, so there's no partial-day merge to reconcile across multiple cycles. Runs
  inline on every cycle; before the app has been running a full 8 days, the query simply finds
  nothing and is a cheap no-op.

## Dashboard UI

A small panel below the existing domain table in `Dashboard.razor`, loaded the same way the rest of
the page's data is (a fresh `IDbContextFactory<DotMarcDbContext>`-created context per load, matching
the existing convention). Shows the single most recent `PollCycle` row:

- **Last polled**: the row's `PolledUtc`, formatted with .NET's `"O"` (round-trip) format specifier
  — a valid ISO 8601 timestamp, e.g. `2026-08-25T18:42:03.0000000+00:00`.
- **Messages checked**, **Reports parsed**, **Parse failures**: the row's counts, plain numbers.
- **Status**: if `Succeeded` is true, no special styling beyond the normal panel; if false, an error
  state (matching the existing red/`Color.Error` convention used elsewhere on this Dashboard for
  problem states) showing the row's `ErrorMessage`.
- If no `PollCycle` row exists yet at all (a brand-new deployment before its first poll cycle has
  completed), the panel shows a neutral "not polled yet" state rather than an error or blank space.

## Testing

Following the existing test suite's `Testcontainers.PostgreSql` pattern (same fixture used by
`PollingServiceTests`/`PollingServiceLeaderLockTests`):

- A poll cycle that processes messages successfully writes exactly one `PollCycle` row with the
  correct `MessagesChecked`/`ReportsParsed`/`ParseFailures` counts and `Succeeded = true`.
- A cycle skipped because another replica holds the leader lock writes no `PollCycle` row (extends
  the existing `RunPollCycleAsync_SkipsPolling_WhenAnotherInstanceHoldsTheLeaderLock` test's
  assertions rather than adding an unrelated new test).
- A cycle where the mailbox fetch itself throws writes a `PollCycle` row with `Succeeded = false`
  and the exception's message, and the exception still propagates out of `RunPollCycleAsync`
  (regression coverage for "the existing failure-logging behavior in `ExecuteAsync` is unchanged").
- Raw `PollCycle` rows for a day more than 7 days in the past are rolled into a
  `PollCycleDailySummary` row with correct aggregate counts and then deleted; rows for a day within
  the 7-day window are left alone.
- Rolling up a day with a mix of successful and failed cycles produces correct `SuccessfulCycles`/
  `FailedCycles` counts on the summary row.
- Dashboard's poll-status panel: this project has no Blazor component-rendering test framework
  (no bUnit anywhere in the suite — the existing pages are covered only at the data/logic layer they
  call, not their markup), so this stays consistent with that: no automated test for the panel's
  Razor markup itself, verified instead by careful code review plus a manual check when the
  environment allows it (same caveat that applied to the manage-domains page).
