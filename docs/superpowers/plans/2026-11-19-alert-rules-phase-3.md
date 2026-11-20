# Alert destinations and ticket mode (Alert rules, Phase 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the three fixed alert channels with any number of named destinations that each rule chooses, and add a per-rule ticket mode.

**Architecture:** `AlertDestination` rows plus an `AlertRuleDestination` join, two new rule fields (`DestinationMode`, `TicketMode`), a pure routing function that picks the destinations for an alert, and an `AlertDestinationService` for audited CRUD. The migration copies each configured channel into a destination so an upgraded install sends to the same places.

**Tech Stack:** .NET 10, EF Core + Npgsql, Blazor Server + MudBlazor, xUnit with the Postgres container fixture.

**Spec:** [docs/superpowers/specs/2026-11-19-alert-destinations-design.md](../specs/2026-11-19-alert-destinations-design.md) (parent: [2026-11-19-alert-rules-design.md](../specs/2026-11-19-alert-rules-design.md)).

## Global Constraints

- No em dashes in any prose (code comments, docs, UI copy, commit messages).
- Meaningful variable names, even in test snippets (no `$r`, `$m`, single letters).
- Warnings fail the build (`TreatWarningsAsErrors`). Muted text uses the `mud-text-secondary` class, never `Color.Secondary`.
- A migrated install must send to the same places as before: the old `NotificationSettings` channel columns stay for one release and are no longer read.
- Destination URLs are secrets: audited with `AuditChanges.Secret`, never written to the audit value, never shown without `AlertsManage`.
- Managing destinations needs `Permission.AlertsManage`. Choosing them and ticket mode on a rule needs `Permission.AlertRulesManage`.
- Destination kind reuses the existing `AlertChannel` enum (`Teams`, `Slack`, `GenericWebhook`). Payloads do not change.
- `website/data/openapi/dotmarc-api.json` must be regenerated (`node scripts/update-openapi.mjs`) when the audit action list changes.
- Version stays 0.13.0. Branch is `develop`, one commit per task, no push without the user's go-ahead.

## Review Focus

- A channel that was switched on but has no URL must not become a destination, and a switched-off channel with a URL becomes a disabled destination (Task 1).
- A rule that selects a destination which is then disabled sends nowhere for that destination. It must not fall back to "all" (Task 2).
- Deleting the only destination a rule selects leaves the rule `Selected` with an empty list, so it goes quiet. This is by design; the delete dialog must say how many rules select the destination (Tasks 3 and 6).
- An alert whose rule was deleted (`AlertRuleId` null) and alerts raised with no rule use `All` (Task 2).
- `Always` for an alert with no domain row (system alerts) or a domain with no PSA company must not create anything or throw (Task 4).
- The rules page must never expose a destination URL to someone without `AlertsManage` (Task 6).

---

### Task 1: Data model and migration

**Files:**
- Create: `src/DotMarc/Notifications/AlertDestination.cs`
- Modify: `src/DotMarc/Notifications/AlertRule.cs`, `src/DotMarc/Data/DotMarcDbContext.cs`
- Create: a migration `AddAlertDestinations` (`dotnet ef migrations add`)
- Test: `test/DotMarc.Tests/Notifications/AlertDestinationMigrationTests.cs`

**Interfaces:**
- Produces:
  - `sealed class AlertDestination { int Id; string Name; AlertChannel Kind; string Url; bool Enabled; DateTimeOffset CreatedUtc; }` (`Kind` stored as string, max 20; `Name` max 100; `Url` max 2000).
  - `sealed class AlertRuleDestination { int AlertRuleId; int AlertDestinationId; }` (composite key, cascade delete on both foreign keys).
  - `enum AlertDestinationMode { All, Selected }`, `enum AlertTicketMode { FollowRules, Never, Always }` (stored as strings, max 20).
  - `AlertRule.DestinationMode` (default `All`), `AlertRule.TicketMode` (default `FollowRules`), `AlertRule.Destinations` (`List<AlertRuleDestination>`).
  - `DotMarcDbContext.AlertDestinations`, `AlertRuleDestinations` DbSets; unique index on `lower(Name)` like the API key name index in migration `20261005171738`.

- [ ] **Step 1: Write the failing test** `AlertDestinationMigrationTests`, following `PsaDataMigrationTests`' pattern (migrate to the migration before this one, update the settings row with raw SQL, migrate forward, read destinations). Cases, with these assertions:
  - Teams on with URL, Slack off with URL, Generic on without URL: exactly two destinations, `("Teams", Teams, url, true)` and `("Slack", Slack, url, false)`.
  - Names are exactly `Teams`, `Slack`, `Generic webhook`.
  - Existing rules read back `DestinationMode == All` and `TicketMode == FollowRules`.
- [ ] **Step 2: Run it** (`dotnet test --filter AlertDestinationMigrationTests`). Expected: FAIL, types do not exist.
- [ ] **Step 3: Add the entity classes, rule fields and DbContext configuration**, then `dotnet ef migrations add AddAlertDestinations` from `src/DotMarc`. Edit the generated `Up` to add `migrationBuilder.Sql` that inserts one `AlertDestinations` row per channel with a non-blank URL (name, kind, URL, `Enabled` from the channel switch, `CreatedUtc` now) after the table is created.
- [ ] **Step 4: Run the test and the existing migration tests.** Expected: PASS.
- [ ] **Step 5: Commit** `Add alert destinations and per-rule delivery fields`.

### Task 2: Routing and sending

**Files:**
- Create: `src/DotMarc/Notifications/AlertDestinationRouting.cs`
- Modify: `src/DotMarc/Notifications/AlertWebhookClient.cs`, `AlertingService.cs` (around `EnsureAlertCoreAsync`), `test/DotMarc.Tests/Internal/FakeAlertWebhookClient.cs`, `AlertWebhookClientTests.cs`
- Test: `test/DotMarc.Tests/Notifications/AlertDestinationRoutingTests.cs`, `AlertDestinationDeliveryTests.cs`

**Interfaces:**
- Consumes: Task 1 types.
- Produces:
  - `static class AlertDestinationRouting { static IReadOnlyList<AlertDestination> Resolve(AlertRule? rule, IReadOnlyCollection<AlertDestination> all); }`: `rule` null or `All` returns every enabled destination with a non-blank URL; `Selected` returns the enabled, non-blank-URL destinations whose id is in `rule.Destinations`.
  - `IAlertWebhookClient.SendAlertAsync(IReadOnlyList<AlertDestination> destinations, string domainName, string alertType, string severity, string title, string message, CancellationToken)` (replaces the `NotificationSettings` parameter; a failure on one destination is logged and the rest still send).
  - `IAlertWebhookClient.SendTestAsync(AlertChannel channel, string? webhookUrl, CancellationToken)` unchanged.

- [ ] **Step 1: Write failing tests.**
  - `AlertDestinationRoutingTests` (pure): rule null gives all enabled; `All` skips disabled and blank-URL; `Selected` with two of three gives those two; `Selected` with a disabled selected destination omits it and does not add others; `Selected` empty gives none.
  - `AlertDestinationDeliveryTests` (Postgres, through `AlertingService` with `FakeAlertWebhookClient`): a rule with `Selected` [B] sends only to B; master switch off sends to nobody; an alert whose rule row was deleted (`AlertRuleId` null) sends to all enabled.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.** `Resolve` as specified. `AlertWebhookClient.SendAlertAsync` loops the given destinations, dispatching on `Kind` to the existing three clients (keep the per-destination try/catch and log with the destination name). In `EnsureAlertCoreAsync`, load the rule with `Include(Destinations)` when `ruleId` is set, load all `AlertDestinations` (no tracking), and call `Resolve`; pass an empty list when `settings.Enabled` is false. Update `FakeAlertWebhookClient` to record destination names too (`Sent` keeps its current tuple shape so existing assertions still compile; add `Destinations`).
- [ ] **Step 4: Update `AlertWebhookClientTests`** for the new signature, then run the whole `Notifications` test namespace. Expected: PASS.
- [ ] **Step 5: Commit** `Send each alert to the destinations its rule chooses`.

### Task 3: Destination service

**Files:**
- Create: `src/DotMarc/Notifications/AlertDestinationService.cs`
- Modify: `src/DotMarc/Audit/AuditActions.cs` (add `AlertDestinationCreated/Changed/Deleted` = `alert_destination.created/changed/deleted` and their entries in the list), `src/DotMarc/Audit/AuditTarget.cs` (`For(AlertDestination)`), `NotificationSettingsService.SaveAsync` (stop saving and auditing the three channel switches and URLs), `test/DotMarc.Tests/.../AuditCoverageTests.cs` (add `typeof(AlertDestinationService)`)
- Test: `test/DotMarc.Tests/Notifications/AlertDestinationServiceTests.cs`

**Interfaces:**
- Consumes: Task 1 types, `WebhookUrls.Problem(url, label)`.
- Produces:
  - `record AlertDestinationInput(string Name, AlertChannel Kind, string Url, bool Enabled)`.
  - `class AlertDestinationException(string message) : Exception`.
  - `static class AlertDestinationService`: `Task<AlertDestination> CreateAsync(DotMarcDbContext, AuditActor, AlertDestinationInput, CancellationToken)`, `Task UpdateAsync(DotMarcDbContext, AuditActor, int id, AlertDestinationInput, CancellationToken)`, `Task<int> RuleCountAsync(DotMarcDbContext, int id, CancellationToken)` (rules that select it, counting `Selected` rules only), `Task DeleteAsync(DotMarcDbContext, AuditActor, int id, CancellationToken)`.

- [ ] **Step 1: Write failing tests.** Create stores and audits (the audit entry's changes contain no URL text: assert the serialized changes do not contain the URL); blank name, duplicate name (case-insensitive), over-long name, and an invalid URL each throw `AlertDestinationException` with a message naming the field; an unchanged update writes no audit row; `RuleCountAsync` counts only `Selected` rules that link the destination; delete removes the destination and its join rows and audits; deleting the only destination a rule selects leaves that rule `Selected` with an empty list (the quiet case in Review Focus).
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** with the same conventions as `AlertRuleService` (audit through `AuditLog.Record`, `AuditChanges.Field` for name, kind, enabled, `Secret("Webhook URL", changed)` for the URL, trimmed name, URL validated by `WebhookUrls.Problem`).
- [ ] **Step 4: Run the new tests, `AuditCoverageTests`, and the settings-service tests** (update any that expected the retired channel fields to save). Expected: PASS.
- [ ] **Step 5: Commit** `Add the alert destination service`.

### Task 4: Ticket mode

**Files:**
- Modify: `src/DotMarc/Notifications/AlertTicketPolicy.cs`, `PsaTicketService.cs` (`CreateTicketAsync` and its caller)
- Test: extend the existing `AlertTicketPolicy` tests; create `test/DotMarc.Tests/Notifications/AlertTicketModeTests.cs`

**Interfaces:**
- Consumes: `AlertRule.TicketMode`, `AlertEvent.AlertRuleId`.
- Produces: `AlertTicketPolicy.ShouldCreateTicket(string alertType, Domain domain, PsaKind psa, IReadOnlyCollection<AlertTicketRule> rules, AlertTicketMode mode = AlertTicketMode.FollowRules)`: `Never` returns false, `Always` returns true, `FollowRules` is the current logic.

- [ ] **Step 1: Write failing tests.** Policy: `Never` is false even when the group rule says create; `Always` is true even when the group rule says no and the type defaults to no; `FollowRules` unchanged. Service (`PsaTestSupport` and `FakePsaProvider`): an alert whose rule is `Always` for a type that does not create tickets by default creates one; an alert whose rule is `Never` creates none; a deleted rule (null `AlertRuleId`) follows the rules; `Always` for a domain with no linked PSA company creates nothing and does not throw.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.** In `PsaTicketService.CreateTicketsAsync`, read the mode once from `context.AlertRules` by `alert.AlertRuleId` (no tracking, `FollowRules` when null or missing) and pass it into `ShouldCreateTicket`.
- [ ] **Step 4: Run** the policy, ticket service and PSA tests. Expected: PASS.
- [ ] **Step 5: Commit** `Let a rule choose whether its alerts open tickets`.

### Task 5: Rule input, validation, audit, reset

**Files:**
- Modify: `src/DotMarc/Notifications/AlertRuleValidator.cs` (`AlertRuleInput`), `AlertRuleService.cs`, `AlertRulePresentation.cs` (`ToInput`), `AlertRuleSeeder.cs`
- Test: extend `AlertRuleServiceTests`, `AlertRuleValidatorTests`, `AlertRulePresentationTests`

**Interfaces:**
- Consumes: Tasks 1 and 3.
- Produces: `AlertRuleInput` gains `AlertDestinationMode DestinationMode`, `IReadOnlyList<int> DestinationIds`, `AlertTicketMode TicketMode` (added after `Scope`, so named arguments at existing call sites keep working; update call sites and the `AlertRuleTestSupport` helpers). `AlertRuleService` creates and updates the join rows and the two fields; `ResetToDefaultAsync` restores `All`, no join rows, `FollowRules`.

- [ ] **Step 1: Write failing tests.** Create with `Selected` [A, B] stores two join rows; update replaces the list and audits `Destinations` (names, not URLs) and `Ticket mode` as changed fields; `Selected` with an unknown destination id is refused with a message naming the problem; `All` ignores and clears any ids; ticket mode is accepted for every kind (the UI decides what to show) and round-trips; reset restores defaults; `ToInput(rule)` carries the new fields; the default pack seeds `All` and `FollowRules`.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** in the validator (check ids against `AlertDestinations`, so the validator needs the set of existing ids passed in, or the service checks it; keep `Problems(kind, input)` pure by having the service do the existence check and throw `AlertRuleValidationException`) and the service.
- [ ] **Step 4: Run** the whole `AlertRule*` and `AuditCoverage` tests. Expected: PASS.
- [ ] **Step 5: Commit** `Save destinations and ticket mode on alert rules`.

### Task 6: UI

**Files:**
- Modify: `src/DotMarc/Components/Pages/AlertsSettings.razor`, `Components/Dialogs/AlertRuleDialog.razor`, `Components/Pages/AlertRules.razor`
- Create: `src/DotMarc/Components/Dialogs/AlertDestinationDialog.razor`
- Test: extend `test/DotMarc.Tests/Components/AlertRulesPageTests.cs` and the rules permission tests; manual browser check

**Interfaces:**
- Consumes: Tasks 2 to 5 (`AlertDestinationService`, `AlertRuleInput` fields, `IAlertWebhookClient.SendTestAsync`).

- [ ] **Step 1: Write failing component tests.** The rules page rendered for someone with `AlertsView` but not `AlertsManage` shows destination names and never any destination URL (render a destination with a distinctive URL and assert the markup lacks it). The rule dialog for a `Selected` rule lists destinations with the selected ones ticked, and says "No destinations selected: this rule's alerts will not be sent anywhere" when none are ticked.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.**
  - Alert settings: replace the three channel cards with a Destinations table (name, kind, enabled switch, Edit, Delete, Send test) and an Add destination button, shown with the existing `AlertsManage` policy. The delete confirmation names the count from `RuleCountAsync`. The dialog edits `AlertDestinationInput` and reports `AlertDestinationException` messages inline. Send test calls `SendTestAsync(kind, url)`.
  - Rule dialog: a Destinations section (radio All or Selected, a checklist of destinations when Selected) and a Ticket mode choice (Follow the ticket rules, Never, Always), disabled with an explanatory tooltip for users who can view but not manage rules.
  - Rule list row summary: append "Sends to: A, B" or "Sends nowhere" when `Selected`, and "Tickets: always" or "Tickets: never" when not `FollowRules`.
- [ ] **Step 4: Run component tests, then verify in Edge** with the recipe in the project notes (Postgres container, demo admin): add, edit, test (expect the validation message with no URL), disable and delete a destination; create a rule selecting one; check the rules page as the scoped Viewer persona is still refused. Stop the app and container afterwards.
- [ ] **Step 5: Commit** `Manage destinations and choose them on a rule`.

### Task 7: Demo, docs, OpenAPI

**Files:**
- Modify: `src/DotMarc/Demo/DemoDataSeeder.cs` (truncate the new tables on reset, no destinations seeded), `website/docs/alerts.mdx`, `website/docs/permissions-and-access.mdx`, `website/docs/updating.mdx` (a note that existing channels became destinations), `website/data/openapi/dotmarc-api.json`
- Test: extend `DemoAlertRulesTests`; `OpenApiDocumentTests` must pass

- [ ] **Step 1: Write the failing test.** Add a destination and a join row, reset the demo twice: the destination tables are empty and every demo rule is `All` and `FollowRules`.
- [ ] **Step 2: Run.** Expected: FAIL (the destination survives the reset).
- [ ] **Step 3: Implement** the reset change, then write the docs: a Destinations section in `alerts.mdx` (what a destination is, All versus Selected, the quiet case, ticket modes, who can do what, the upgrade note), the permission wording, and the `updating.mdx` note. Run `node scripts/update-openapi.mjs`.
- [ ] **Step 4: Run the full suite** to a file and read the tail. Expected: all pass, 0 failed.
- [ ] **Step 5: Commit** `Document destinations and ticket mode`.
