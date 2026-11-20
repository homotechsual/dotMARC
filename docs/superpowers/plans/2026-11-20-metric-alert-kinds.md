# Metric alert kinds (Alert rules, Phase 4b) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add three metric alert kinds (DMARC pass rate low, Message volume changed, Rejected or quarantined messages high) that an administrator can switch on, tune and scope from the Alert rules page.

**Architecture:** Three new `AlertTypes` and `AlertRuleKinds` entries with their own parameters, pure evaluator functions over a domain's recent reports, and one new step in the monitor cycle that runs them through the existing rule machinery. Each kind ships one default rule that is switched off, using a new `EnabledByDefault` on the kind.

**Tech Stack:** .NET 10, EF Core + Npgsql, Blazor Server + MudBlazor, xUnit with the Postgres container fixture.

**Spec:** [docs/superpowers/specs/2026-11-20-metric-alert-kinds-design.md](../specs/2026-11-20-metric-alert-kinds-design.md) (parents: [quiet hours](../specs/2026-11-19-quiet-hours-escalation-design.md), [alert rules](../specs/2026-11-19-alert-rules-design.md)).

## Global Constraints

- No em dashes in any prose (code comments, docs, UI copy, commit messages). Meaningful variable names everywhere, including tests.
- Warnings fail the build. Muted text uses the `mud-text-secondary` class, never `Color.Secondary`.
- Alert type keys are stored forever and never change: `PassRateLow`, `VolumeChanged`, `RejectedMessagesHigh`. Parameter keys are stored in rule JSON and never change.
- An upgrade must raise nothing: all three default rules are `Enabled = false`, and "Reset to default values" restores them to off.
- Detection reads only the reports the cycle already loads (the last 30 days, `DomainStatistics.ReportWindow`), by each report's `ReceivedUtc`, for monitored domains only. Message counts use each record's `MessageCount`.
- Rules load with more than one collection use `AsSplitQuery()`; test contexts that exercise rule services throw on `MultipleCollectionIncludeWarning`, as the app does.
- `website/data/openapi/dotmarc-api.json` is regenerated (`node scripts/update-openapi.mjs`) because the alert type lists change. Version stays 0.13.0. Branch `develop`, one commit per task, no push without the user's go-ahead.
- Scratch scripts for patching files go in the scratchpad directory, written with the Write tool (shell heredocs have failed in this repo), run with a unique file name.

## Review Focus

- An upgrade raises nothing, and the three rules reset to off (Task 1).
- A pass rate or volume rule never fires on a handful of messages, and a volume rule never fires when the earlier period is under the minimum, including zero (Task 2).
- Reject and quarantine count messages (`MessageCount`), not reports or records (Task 2).
- A report exactly at a window edge, and a domain with no reports at all, behave as written (Tasks 2 and 3).
- An alert closes by itself when the number recovers, and when its rule is switched off or no longer applies (Task 3).
- A rule scoped to one group raises only for that group, and two stacked rules each raise their own alert (Task 3).

---

### Task 1: The three kinds, off by default

**Files:**
- Modify: `src/DotMarc/Notifications/AlertTypes.cs`, `AlertRuleKinds.cs`, `AlertRuleParameters.cs` (keys and `Text`), `AlertRuleSet.cs` (`ResolvedAlertRule.Text`), `AlertRuleSeeder.cs` (`DefaultPack`), `AlertRuleService.cs` (`ResetToDefaultAsync`)
- Test: `test/DotMarc.Tests/Notifications/MetricAlertKindsTests.cs`; fix any existing test that assumed every default rule is on

**Interfaces:**
- Produces:
  - `AlertTypes.PassRateLow`, `VolumeChanged`, `RejectedMessagesHigh` constants and `AlertTypeInfo` entries with `CreatesTicketByDefault: false`: "DMARC pass rate low", "Message volume changed", "Rejected or quarantined messages high", each with a one-sentence description.
  - `AlertRuleParameterKeys.BelowPercent = "belowPercent"`, `WindowDays = "windowDays"`, `Direction = "direction"`, `ChangePercent = "changePercent"`, `MessagesAtLeast = "messagesAtLeast"` (`MinimumMessages` exists and is reused).
  - `AlertRuleKind` gains a trailing `bool EnabledByDefault = true` (existing constructions still compile).
  - Kind definitions (all `HasDomainScope`, default severity `Warning`, no auto close, `EnabledByDefault: false`) with these parameters, each with a label and a help sentence:
    - `PassRateLow`: `belowPercent` 1 to 99 default 90; `windowDays` 1 to 30 default 7; `minimumMessages` 1 to 1000000 default 100.
    - `VolumeChanged`: `direction` choice `drop`/`rise`/`either` default `drop`; `changePercent` 10 to 500 default 50; `windowDays` 1 to 15 default 7; `minimumMessages` 1 to 1000000 default 100.
    - `RejectedMessagesHigh`: `messagesAtLeast` 1 to 1000000 default 50; `windowDays` 1 to 30 default 7.
  - `AlertRuleParameters.Text(string key, string fallback)` and `ResolvedAlertRule.Text(string key)` (the rule's value, or the kind's default).
  - `DefaultPack` sets `Enabled = kind.EnabledByDefault && <existing legacy check>`; `ResetToDefaultAsync` restores `Enabled = kind.EnabledByDefault`.

- [ ] **Step 1: Write failing tests** in `MetricAlertKindsTests`. Catalogue: the three kinds exist with the parameter keys, ranges and defaults above (assert each definition's `Min`, `Max`, `Default`), are domain-scoped, and their alert types have `CreatesTicketByDefault == false`. Validation (Postgres-free, `AlertRuleValidator.Problems`): each parameter just outside its range is refused with a message naming it, `direction` outside the choices is refused, defaults pass. Seeding (Postgres): after `AlertRuleSeeder.EnsureDefaultPackAsync` on a migrated database the three default rules exist and are disabled, with every parameter stored; on a database that already has the earlier default pack (seed it, delete the three rules, seed again) only the three are added and the others are untouched; resetting a default metric rule that was switched on (`AlertRuleService.ResetToDefaultAsync`) leaves it off.
- [ ] **Step 2: Run** (`dotnet test --filter MetricAlertKindsTests`). Expected: FAIL.
- [ ] **Step 3: Implement** the constants, kinds, parameter keys, accessors, seeding and reset. The existing legacy settings helper needs no change: its fallthrough already returns the kind's defaults.
- [ ] **Step 4: Run the new tests, then every `AlertRule*`, `Demo*` and `OpenApi*` test.** Fix existing tests that count kinds or assume every default rule is on (adjust the expectation, not the production code). Expected: PASS.
- [ ] **Step 5: Commit** `Add the metric alert kinds, switched off`.

### Task 2: The evaluators

**Files:**
- Create: `src/DotMarc/Notifications/MetricAlertEvaluator.cs`
- Test: `test/DotMarc.Tests/Notifications/MetricAlertEvaluatorTests.cs`

**Interfaces:**
- Consumes: Task 1 keys; `Report` (`ReceivedUtc`, `Records`), `ReportRecord` (`MessageCount`, `Disposition`, `SpfResult`, `DkimResult`), `DomainStatistics.IsPassing`.
- Produces (all pure, `nowUtc` passed in, results are records with the numbers needed for the message):
  - `MetricAlertEvaluator.PassRate(IEnumerable<Report> reports, int windowDays, int belowPercent, int minimumMessages, DateTimeOffset nowUtc)` returning `PassRateResult(bool Raise, int Messages, double PassPercent)`: raises when messages in the window are at least `minimumMessages` and the percentage passing is below `belowPercent`. A message passes when its record `IsPassing`.
  - `MetricAlertEvaluator.Volume(IEnumerable<Report> reports, int windowDays, string direction, int changePercent, int minimumMessages, DateTimeOffset nowUtc)` returning `VolumeResult(bool Raise, int Current, int Previous, double ChangePercent)`: current window is the last `windowDays` days, previous is the `windowDays` before that. Raises only when `Previous >= minimumMessages` and the change meets the direction: `drop` when `Current <= Previous * (100 - changePercent) / 100`, `rise` when `Current >= Previous * (100 + changePercent) / 100`, `either` when either holds. `ChangePercent` is signed, `(Current - Previous) / Previous * 100`.
  - `MetricAlertEvaluator.Rejected(IEnumerable<Report> reports, int windowDays, int messagesAtLeast, DateTimeOffset nowUtc)` returning `RejectedResult(bool Raise, int Messages)`: sums `MessageCount` of records with `Disposition` of `Reject` or `Quarantine`; raises at `messagesAtLeast` or more.
  - A report is in a window when `ReceivedUtc > nowUtc - window` and `ReceivedUtc <= nowUtc` (the start edge excluded, the end included); the previous window is the same shape one window earlier.

- [ ] **Step 1: Write failing tests** (pure, builders for `Report` and `ReportRecord`). Pass rate: 80 of 100 passing in the window with `belowPercent` 90 and minimum 100 raises with 80%; 99 messages never raises however bad; exactly at the threshold (90 of 100 with `belowPercent` 90) does not raise; reports older than the window are ignored; no reports does not raise. Volume: 100 previous and 40 current with drop 50 raises (-60%); 100 and 60 does not; exactly 50 raises (at the boundary); previous 99 with minimum 100 does not raise even when current is 0; previous 0 never raises; `rise` raises at 150 current for 100 previous with change 50 and not at 149; `either` raises for both; a report at exactly `nowUtc - window` belongs to the previous window and one at exactly `nowUtc` to the current. Rejected: a record of 30 rejected and 25 quarantined messages with `messagesAtLeast` 50 raises with 55, 49 does not, `None` disposition records do not count, counts use `MessageCount` not the number of records.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** the three functions as static methods.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `Evaluate the metric alerts`.

### Task 3: Raise them in the monitor cycle

**Files:**
- Modify: `src/DotMarc/Notifications/AlertingService.cs` (`CheckPinnedDomainsAsync` loop, `CycleKinds`, a new `CheckMetricAlertsAsync`)
- Test: `test/DotMarc.Tests/Notifications/MetricAlertingTests.cs`

**Interfaces:**
- Consumes: Tasks 1 and 2; the existing helpers used by `CheckSuspiciousRejectActivityAsync` (`applicable.Record`, `rules.For`, `EnsureAlertAsync`, `ResolveAlertAsync`, `openAlerts.Has`/`HasAny`).
- Produces: `CheckMetricAlertsAsync(db, settings, rules, domain, facts, openAlerts, applicable, cancellationToken)` called for each monitored domain after the sender checks; the three kinds are added to `CycleKinds` so their open alerts load and are swept. Alert titles: "DMARC pass rate is low", "Message volume changed", "Rejected or quarantined messages are high". Messages: "contoso.io: DMARC pass rate was 71% over the last 7 days (threshold 90%, 420 messages).", "contoso.io: message volume fell 62% over the last 7 days (340 messages, 900 in the 7 days before).", "contoso.io: 55 messages were rejected or quarantined over the last 7 days (threshold 50)." ("rose" and a plus-free percentage for a rise).

- [ ] **Step 1: Write failing tests** (Postgres, `FakeAlertWebhookClient`, a monitored domain with reports and records seeded by the test, rules edited through `AlertRuleTestSupport.EditDefaultRuleAsync` or created through `AlertRuleService`). With the default (off) rules nothing is raised however bad the data. Pass rate: switching the default rule on raises one alert with the message above for a domain at 71% over 420 messages; running again within the cooldown raises no second alert; after reports with good data arrive the next cycle resolves the alert; a domain with 50 messages never alerts. Volume: raises for a drop and resolves when volume recovers. Rejected: raises at the threshold, counts both dispositions. Scope: a rule scoped to group A raises for a domain in A and not for one in B with the same data, and with the default rule on as well (stop processing off on the scoped rule) both raise their own alert for the A domain. Disabling the rule closes its open alert on the next cycle. A domain with no reports raises nothing for any of the three. Each raised alert has the rule's severity and delivers through the normal path (the fake client sees one call).
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** `CheckMetricAlertsAsync` following `CheckSuspiciousRejectActivityAsync`: for each kind, `applicable.Record(domain.Name, kind, rules.For(kind, facts))`; with no applicable rules resolve any open alert of the kind; otherwise for each rule evaluate with its parameters (`rule.Int`, `rule.Text`) at the service clock's now, raise through `EnsureAlertAsync` when the evaluator says so, else resolve that rule's open alert.
- [ ] **Step 4: Run the new tests and the whole `Notifications` namespace.** Expected: PASS.
- [ ] **Step 5: Commit** `Raise metric alerts from the monitor cycle`.

### Task 4: Docs, API list, browser check

**Files:**
- Modify: `website/docs/alerts.mdx` (alert types table and a new Metric alerts section), `website/data/openapi/dotmarc-api.json`
- Test: extend `OpenApiDocumentTests` only if its expectations name alert types (run it)

- [ ] **Step 1: Write the failing test** only if an existing test pins the alert type list or the docs; otherwise this step is the browser check below. Run `OpenApiDocumentTests` and `AlertTypes`-related tests first and fix expectations that name the old list.
- [ ] **Step 2: Docs.** Add the three types to the Alert types table. Add a "Metric alerts" section: what each does and its settings with ranges and defaults, that all three are off until a rule is switched on, that they use the last 30 days of reports (so the volume window is at most 15 days), the minimum-messages guard, that alerts close by themselves when the number recovers, an example of a scoped copy for one client, and that they appear in ticket rules and the API like other types. Regenerate the OpenAPI copy.
- [ ] **Step 3: Verify in Edge** with the project's recipe (Postgres container `dotmarc-verify` on 55432, `Demo__Enabled`, demo admin; stop the app and the container afterwards): the Alert rules page lists the three kinds, each with one default rule that is off; edit one, see its settings and help, set a value outside the range and read the refusal message; switch a default rule on and see the row; add a scoped copy.
- [ ] **Step 4: Run the full suite** to a file and read the tail. Expected: all pass, 0 failed.
- [ ] **Step 5: Commit** `Document the metric alerts`.
