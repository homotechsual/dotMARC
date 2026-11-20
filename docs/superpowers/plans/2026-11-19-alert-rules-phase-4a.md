# Quiet hours, escalation and rule read endpoints (Alert rules, Phase 4a) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a rule hold or mute its notifications during a daily quiet window, escalate to extra destinations when its alert stays open, and let the API read rules and destination names.

**Architecture:** New rule fields and an escalation join table, delivery state on the alert (`NotifiedUtc`, `EscalatedUtc`), a pure quiet-window function, two new steps in the existing monitor cycle (send held alerts, escalate), and read-only API endpoints. No new table of deliveries.

**Tech Stack:** .NET 10, EF Core + Npgsql, Blazor Server + MudBlazor, minimal API with the project's `ApiFilterReader`/`ApiProblems` helpers, xUnit with the Postgres container fixture.

**Spec:** [docs/superpowers/specs/2026-11-19-quiet-hours-escalation-design.md](../specs/2026-11-19-quiet-hours-escalation-design.md) (parents: [destinations](../specs/2026-11-19-alert-destinations-design.md), [alert rules](../specs/2026-11-19-alert-rules-design.md)).

## Global Constraints

- No em dashes in any prose (code comments, docs, UI copy, commit messages). Meaningful variable names everywhere, including tests.
- Warnings fail the build. Muted text uses the `mud-text-secondary` class, never `Color.Secondary`.
- Wherever rules are loaded with more than one collection (`Scope`, `Destinations`, `EscalationDestinations`) use `AsSplitQuery()`: the app throws on `MultipleCollectionIncludeWarning`. Test database contexts for rule tests are configured to throw on it too, as `AlertRuleDeliveryFieldsTests.CreateContext` is.
- A migrated install must behave as before: every existing alert gets `NotifiedUtc = CreatedUtc`, so nothing old is sent again, and every existing rule is `QuietMode.Off`, no escalation.
- Quiet hours affect notifications only. Alerts are still raised and recorded, and tickets still open.
- Acknowledging an alert resolves it (`AlertAcknowledgement`), so "still unacknowledged" means "still open" (`!IsResolved`).
- Webhook URLs never appear in the API, the audit log, or any page except the destination dialog.
- Choosing quiet hours and escalation needs `AlertRulesManage`. The time zone needs `AlertsManage`. The API endpoints need `AlertsView` and a key with no group limit (`ApiScope.IsScoped` false).
- Time zone ids are IANA ids resolved with `TimeZoneInfo.FindSystemTimeZoneById`; an unknown id behaves as `UTC` rather than failing.
- `website/data/openapi/dotmarc-api.json` is regenerated (`node scripts/update-openapi.mjs`) when API models, endpoints or the audit action list change. Version stays 0.13.0. Branch `develop`, one commit per task, no push without the user's go-ahead.
- Scratch scripts for patching files go in the scratchpad directory, written with the Write tool (shell heredocs have failed in this repo), run with a unique file name.

## Review Focus

- Existing alerts are never re-sent after the upgrade (Task 1).
- A held alert that is resolved or acknowledged before the window ends is never sent; a held alert is sent exactly once after it (Task 3).
- Escalation fires once per alert, never for a resolved alert, and never while the alert is still held (Task 3).
- A window that crosses midnight, and a window across a daylight-saving change, behave as written in local time (Task 2).
- The Critical switch works both ways: on, Critical alerts are delivered inside a window; off, they are held or muted like the rest (Tasks 2 and 3).
- An unknown time zone id falls back to UTC instead of throwing (Tasks 2 and 4).
- A group-limited API key gets no rules and an empty destination list, and no response ever contains a URL (Task 6).

---

### Task 1: Data model and migration

**Files:**
- Modify: `src/DotMarc/Notifications/AlertDestination.cs` (enum and join class), `AlertRule.cs`, `AlertEvent.cs`, `NotificationSettings.cs`, `src/DotMarc/Data/DotMarcDbContext.cs`
- Create: a migration `AddQuietHoursAndEscalation` (`dotnet ef migrations add`, run from `src/DotMarc`)
- Test: `test/DotMarc.Tests/Notifications/QuietHoursMigrationTests.cs`

**Interfaces:**
- Produces:
  - `enum AlertQuietMode { Off, Hold, Mute }` (stored as string, max 20).
  - `AlertRule.QuietMode` (default `Off`), `QuietStartMinutes` and `QuietEndMinutes` (`int?`, minutes after local midnight), `QuietIgnoredForCritical` (`bool`, default true), `EscalateAfterMinutes` (`int?`), `EscalationDestinations` (`List<AlertRuleEscalationDestination>`).
  - `sealed class AlertRuleEscalationDestination { int AlertRuleId; int AlertDestinationId; }` (composite key, cascade delete on both foreign keys, DbSet `AlertRuleEscalationDestinations`).
  - `NotificationSettings.TimeZoneId` (`string`, default `"UTC"`, max 100).
  - `AlertEvent.NotifiedUtc` and `AlertEvent.EscalatedUtc` (`DateTimeOffset?`).

- [ ] **Step 1: Write the failing test** `QuietHoursMigrationTests`, following `AlertDestinationMigrationTests` (migrate to `20261119200122_AddAlertDestinations`, insert an alert and a rule with raw SQL, migrate forward, read back). Assert: the existing alert has `NotifiedUtc == CreatedUtc` and `EscalatedUtc == null`; the existing rule reads `QuietMode.Off`, `QuietIgnoredForCritical == true`, `EscalateAfterMinutes == null`, no escalation destinations; the settings row has `TimeZoneId == "UTC"`.
- [ ] **Step 2: Run it** (`dotnet test --filter QuietHoursMigrationTests`). Expected: FAIL, members do not exist.
- [ ] **Step 3: Add the members and DbContext configuration** (`QuietMode` string conversion with default `Off`, default true for the Critical switch, the join table's keys and cascades). Generate the migration, then edit its `Up` to add `migrationBuilder.Sql("UPDATE \"AlertEvents\" SET \"NotifiedUtc\" = \"CreatedUtc\"")` after the column is added.
- [ ] **Step 4: Run the test and the other migration tests** (`FullyQualifiedName~MigrationTests`). Expected: PASS.
- [ ] **Step 5: Commit** `Add quiet hours and escalation fields`.

### Task 2: The quiet window

**Files:**
- Create: `src/DotMarc/Notifications/QuietHours.cs`
- Test: `test/DotMarc.Tests/Notifications/QuietHoursTests.cs`

**Interfaces:**
- Consumes: Task 1 types.
- Produces:
  - `enum QuietDecision { Deliver, Hold, Mute }`.
  - `static class QuietHours` with `static QuietDecision Decide(AlertRule? rule, string severity, DateTimeOffset nowUtc, TimeZoneInfo zone)`: `Deliver` when the rule is null, mode is `Off`, start or end is null, the time is outside the window, or the severity is `Critical` and `QuietIgnoredForCritical`; otherwise `Hold` or `Mute` by the rule's mode. The window is `[start, end)` in local time; start greater than end crosses midnight; start equal to end is treated as no window.
  - `static TimeZoneInfo ZoneFor(string? timeZoneId)`: the zone for the id, `TimeZoneInfo.Utc` for null, blank or unknown ids.
  - `static bool IsValidZone(string? timeZoneId)`.

- [ ] **Step 1: Write failing tests** (pure). Cases, with a rule built in the test: window 22:00 to 07:00 in UTC (`Hold`) at 23:00, 03:00 are `Hold`, at 07:00 and 12:00 are `Deliver`; window 09:00 to 17:00 at 09:00 `Hold`, at 17:00 `Deliver`; `Mute` mode returns `Mute`; Off returns `Deliver`; start equal to end returns `Deliver`; a null rule returns `Deliver`; Critical with the switch on returns `Deliver` inside the window, with the switch off returns `Hold`; zone `Europe/London`, window 01:00 to 02:00, on the spring forward day (2027-03-28) at 01:30 UTC is already 02:30 local, so `Deliver`, and on 2027-03-27 at 01:30 UTC is `Hold`; `ZoneFor("Not/AZone")` is UTC and `IsValidZone` is false for it and true for `Europe/London`.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** `QuietHours` with `TimeZoneInfo.ConvertTime` and minutes-after-midnight arithmetic.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `Work out whether an alert falls in a rule's quiet window`.

### Task 3: Delivery, held alerts and escalation

**Files:**
- Modify: `src/DotMarc/Notifications/AlertingService.cs` (`EnsureAlertCoreAsync`, `CheckPinnedDomainsAsync`, constructor), `AlertDestinationRouting.cs`
- Test: `test/DotMarc.Tests/Notifications/AlertDestinationRoutingTests.cs` (extend), create `test/DotMarc.Tests/Notifications/QuietHoursDeliveryTests.cs`, `EscalationTests.cs`

**Interfaces:**
- Consumes: Tasks 1 and 2.
- Produces:
  - `AlertDestinationRouting.ResolveEscalation(AlertRule rule, IReadOnlyCollection<AlertDestination> all)`: the enabled destinations with a URL whose id is in `rule.EscalationDestinations`; empty when none.
  - `AlertingService` constructor gains a trailing optional `TimeProvider? timeProvider = null` (default `TimeProvider.System`) used for "now" in the new code, so tests control time with `FixedTimeProvider`.
  - A private step `SendHeldAlertsAsync` and `EscalateAlertsAsync` run from `CheckPinnedDomainsAsync` after the existing work.

- [ ] **Step 1: Write failing tests.**
  - Routing: `ResolveEscalation` returns only the ticked, enabled, URL-bearing destinations; none ticked returns empty.
  - `QuietHoursDeliveryTests` (Postgres, `FakeAlertWebhookClient`, `FixedTimeProvider`, a stale monitored domain so the missing-report alert fires): a rule in `Hold` with a window around the fixed time raises the alert with `NotifiedUtc == null` and sends nothing; the next cycle at a time after the window sends once and sets `NotifiedUtc`; a second cycle sends nothing more; if the alert is resolved before the window ends (domain starts reporting) it is never sent; `Mute` sets `NotifiedUtc` and sends nothing, then nothing at the window's end; outside the window sends immediately and sets `NotifiedUtc`; a Critical rule with the switch on sends inside the window, with it off holds; the master switch off sends nothing but still sets `NotifiedUtc`; a bad `TimeZoneId` in settings behaves as UTC.
  - `EscalationTests`: a rule with `EscalateAfterMinutes = 30` and one escalation destination: at 29 minutes after notification nothing escalates, at 31 minutes the alert is sent to that destination only (title starts "Escalated: ") and `EscalatedUtc` is set, and at 90 minutes nothing more is sent; a resolved alert does not escalate; a held alert does not escalate; a muted alert does; a rule with minutes but no escalation destinations sends nothing and does not throw; escalation ignores the quiet window (cycle runs inside it).
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.** In `EnsureAlertCoreAsync`, read the settings' time zone (`QuietHours.ZoneFor(settings.TimeZoneId)`) and the rule (with `Destinations` and `EscalationDestinations`, `AsSplitQuery`), call `QuietHours.Decide` at `timeProvider.GetUtcNow()`, and on `Deliver` send and set `NotifiedUtc`, on `Mute` set `NotifiedUtc` only, on `Hold` leave it null. The held-alert step loads open alerts with `NotifiedUtc == null`, re-decides with the alert's rule, and on `Deliver` sends and sets `NotifiedUtc` (an alert with a deleted rule decides as no rule). The escalation step loads open alerts with `NotifiedUtc != null`, `EscalatedUtc == null` whose rule has `EscalateAfterMinutes` and `NotifiedUtc + minutes <= now`, sends to `ResolveEscalation` with the title prefixed `Escalated: `, and sets `EscalatedUtc` even when there are no destinations so it is not retried forever. Keep the existing per-destination failure logging.
- [ ] **Step 4: Run the new tests and the whole `Notifications` namespace.** Expected: PASS.
- [ ] **Step 5: Commit** `Hold, mute and escalate alert notifications`.

### Task 4: Rule input, validation, audit, time zone setting

**Files:**
- Modify: `src/DotMarc/Notifications/AlertRuleValidator.cs` (`AlertRuleInput`, `Problems`), `AlertRuleService.cs`, `AlertRulePresentation.cs` (`ToInput`, `DescribeDelivery`), `NotificationSettingsService.cs`, `src/DotMarc/Audit/AuditActions.cs` only if a new action is needed (none expected)
- Test: `test/DotMarc.Tests/Notifications/AlertRuleQuietFieldsTests.cs`, extend `AlertRuleValidatorTests`, `AlertDeliveryPresentationTests`, `NotificationSettingsServiceTests`

**Interfaces:**
- Consumes: Tasks 1 to 3.
- Produces: `AlertRuleInput` gains trailing optional parameters `AlertQuietMode QuietMode = Off`, `int? QuietStartMinutes = null`, `int? QuietEndMinutes = null`, `bool QuietIgnoredForCritical = true`, `int? EscalateAfterMinutes = null`, `IReadOnlyList<int>? EscalationDestinationIds = null`. Validator problems (each names the field): mode other than Off needs both times; times 0 to 1439; start equal to end refused; escalation minutes 5 to 10080. The service validates that escalation destination ids exist (like destination ids), stores and audits all new fields (destination names, never URLs), `ResetToDefaultAsync` restores Off, null times, true, null, none. `NotificationSettingsService.SaveAsync` also saves `TimeZoneId`, refusing an invalid id with `ArgumentException("The time zone isn't one dotMARC knows.")`, audited as a normal field. `AlertRulePresentation.DescribeDelivery` adds "Quiet 22:00 to 07:00 (held)" / "(muted)" and "Escalates after 30 minutes." sentences when set.

- [ ] **Step 1: Write failing tests.** Validator: each refusal above; a good window passes. Service (strict-warning context): create with quiet hours and two escalation destinations stores everything; update replaces escalation destinations and audits `Quiet mode`, `Quiet hours`, `Escalate after (minutes)` and `Escalation destinations` by name with no URL text; unknown escalation destination refused; unchanged update writes no audit; reset restores defaults; `ToInput` carries the fields. Settings: a valid zone saves and audits, an invalid id throws. Presentation: the new sentences.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement**, loading rules with `AsSplitQuery` in `AlertRuleService.LoadAsync`.
- [ ] **Step 4: Run** every `AlertRule*`, `NotificationSettings*` and `AuditCoverage` test. Expected: PASS.
- [ ] **Step 5: Commit** `Save quiet hours, escalation and the time zone`.

### Task 5: UI

**Files:**
- Modify: `src/DotMarc/Components/Dialogs/AlertRuleDialog.razor`, `Components/Pages/AlertRules.razor`, `Components/Pages/AlertsSettings.razor`, `Components/Pages/Alerts.razor`
- Test: extend `AlertDeliveryPresentationTests` for any new pure helper; manual browser check

**Interfaces:**
- Consumes: Tasks 3 and 4 (`AlertRuleInput` fields, `QuietHours.IsValidZone`, `AlertEvent.NotifiedUtc`).

- [ ] **Step 1: Write failing tests** for any pure helper added (for example `AlertRulePresentation.DescribeNotification(AlertEvent alert, AlertRule? rule)` returning "Notification held until the quiet window ends." when `NotifiedUtc` is null and the alert is open, "Muted by quiet hours." when the rule's mode is `Mute` and the alert was never sent, otherwise null; test each case and that a resolved alert says nothing).
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.** Dialog: a Quiet hours section (mode select, two time pickers, the Critical switch labelled "Critical alerts ignore quiet hours", a note that the time zone is set on Alert settings) and an Escalation section (minutes field, destination checklist, a warning when minutes are set with none ticked). Rules page loads `EscalationDestinations` with `AsSplitQuery`. Alert settings: a Time zone select (the system's zone ids, searchable, default UTC) saved with the other settings. Alerts page: show the delivery sentence in an alert's detail.
- [ ] **Step 4: Run the tests, then verify in Edge** with the project's recipe (Postgres container `dotmarc-verify` on 55432, `Demo__Enabled`, demo admin; stop the app and the container afterwards): set quiet hours and escalation on a rule and see the row summary, reopen the dialog and see the values kept, toggle the rule's On switch and confirm destinations and quiet settings survive, set a time zone, see a refusal for a start equal to end.
- [ ] **Step 5: Commit** `Choose quiet hours and escalation on a rule`.

### Task 6: API read endpoints

**Files:**
- Create: `src/DotMarc/Api/AlertRuleEndpoints.cs`
- Modify: `src/DotMarc/Api/ApiEndpoints.cs` (map it), `ApiModels.cs`, `ApiTags.cs`
- Test: `test/DotMarc.Tests/Api/AlertRuleEndpointTests.cs` (follow the existing endpoint test setup, for example the alert endpoint tests)

**Interfaces:**
- Consumes: Tasks 1 and 4; `RequirePermission`, `ApiScope.From(user).IsScoped`, `ApiProblems.NotFound`, `ApiFilterReader`, `ApiPage<T>`.
- Produces: `ApiTags.AlertRules = "Alert rules"` with its description added to `ApiTags.All`; models `ApiAlertRule(int Id, string Kind, string Name, bool Enabled, string Severity, int Position, bool StopProcessing, int? CooldownMinutes, int? AutoCloseDays, IReadOnlyDictionary<string,string> Parameters, string DestinationMode, IReadOnlyList<ApiNamedRef> Destinations, string TicketMode, ApiQuietHours QuietHours, ApiEscalation Escalation, string AppliesTo, IReadOnlyList<ApiRuleScopeEntry> Scope)`, `ApiQuietHours(string Mode, string? Start, string? End, bool IgnoredForCritical)` (times as "HH:mm"), `ApiEscalation(int? AfterMinutes, IReadOnlyList<ApiNamedRef> Destinations)`, `ApiRuleScopeEntry(string Target, int Id, bool Exclude)`, `ApiAlertDestination(int Id, string Name, string Kind, bool Enabled)`. Endpoints: `GET /alert-rules` (filters `kind`, `enabled`, `page`, `pageSize`, ordered by kind then position), `GET /alert-rules/{id:int}`, `GET /alert-destinations` (ordered by name). All `AlertsView`; the two rule endpoints return `ApiProblems.NotFound("alert rule")` and the destination list returns an empty page for a group-limited key.

- [ ] **Step 1: Write failing tests.** Unscoped key: list returns every rule with the fields above, filters by `kind` and `enabled`, rejects an unknown kind with a validation problem like the other filters; `{id}` returns one and 404s for a missing id; destinations return names and kinds and the serialized body never contains the destination's URL (create one with a distinctive URL and assert the raw JSON lacks it); a key scoped to a group gets 404 on both rule endpoints and an empty destination page; a key without `AlertsView` is refused.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** the endpoints with `DocumentsFilter` entries like `AlertEndpoints`, loading rules with `AsSplitQuery` and group, tag and domain names for `AppliesTo` using `AlertRulePresentation.DescribeScope`.
- [ ] **Step 4: Run the new tests and `OpenApiDocumentTests`** (regenerate with `node scripts/update-openapi.mjs` first). Expected: PASS.
- [ ] **Step 5: Commit** `Read alert rules and destinations through the API`.

### Task 7: Demo, docs, full suite

**Files:**
- Modify: `src/DotMarc/Demo/DemoDataSeeder.cs`, `website/docs/alerts.mdx`, `website/docs/api.mdx` (or the page that lists the API endpoints, found with a search for `acknowledge`), `website/docs/permissions-and-access.mdx` if wording needs it, `website/data/openapi/dotmarc-api.json`
- Test: extend `DemoAlertRulesTests`

- [ ] **Step 1: Write the failing test.** After a demo reset the new tables are empty and every demo rule is `QuietMode.Off` with no escalation, except one demo rule (a New sender rule for Cobalt Freight already exists; give the Aurora missing-report rule a Hold window of 22:00 to 07:00 so the demo shows the row summary) which has exactly that window and Critical ignoring quiet hours. Add an escalation destination row before the first reset to prove truncation.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement**: truncate `AlertRuleEscalationDestinations`, seed the demo window. Docs: a Quiet hours section and an Escalation section in `alerts.mdx` (modes, midnight crossing, time zone setting, Critical switch, quiet hours affect notifications only, held alerts are sent when the window ends only if still open, escalation once, only to chosen destinations, timing is as precise as the monitor interval), the Alert settings table row for Time zone, the API page entries for the three endpoints, the upgrade note (existing alerts are not re-sent, rules unchanged). Regenerate the OpenAPI copy.
- [ ] **Step 4: Run the full suite** to a file and read the tail. Expected: all pass, 0 failed.
- [ ] **Step 5: Commit** `Document quiet hours, escalation and the rule endpoints`.
