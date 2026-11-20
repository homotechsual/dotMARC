# Alert Rules Phase 1 (Engine and Default Pack) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every alert dotMARC raises is decided by an ordered, scoped rule, with a default pack that reproduces today's behaviour exactly, so no screen changes and no setting stops working.

**Architecture:** Two new tables, `AlertRules` and `AlertRuleScopeEntries`, plus a nullable `AlertEvents.AlertRuleId`. A rule is a built-in kind (the existing `AlertTypes` key) with a name, severity, position, `StopProcessing` flag, optional cooldown and auto-close overrides, a scope (include and exclude entries for groups, tags and domains) and a JSON dictionary of parameter overrides. **A parameter a rule does not set falls back to the global `NotificationSettings` value**, and a rule is also off while its legacy switch is off. So the seeded default pack carries no values of its own, and the existing Alert settings page keeps working untouched; Phase 2 copies the values into the rules and retires those fields. `AlertRuleSet` loads the rules (seeding the default pack on first use), and `Resolve` returns, for one kind and one domain, the enabled matching rules in order up to the first rule that stops processing. `AlertingService` and `DnsHealthAlertEvaluator` iterate those rules: each rule raises and resolves its own alert, keyed by domain, type and rule, and an alert whose rule no longer applies to its domain is resolved.

**Tech Stack:** .NET 10, EF Core + Npgsql (one migration via `dotnet ef migrations add`), xUnit with the Postgres fixture (`[Collection("Postgres")]`).

**Spec:** `docs/superpowers/specs/2026-11-19-alert-rules-design.md` (sections "What a rule is", "Which rule applies", "Migration", "Phases" item 1). Reuses `AlertTypes`, `AlertingService`, `DnsHealthChecks`, `DnsHealthAlertEvaluator`, `NotificationSettings`, `AlertAcknowledgement`.

## Global Constraints

- No em dashes in any copy, comments or docs.
- Behaviour with only the default pack must be identical to today: the existing alerting, DNS health, sender alert, acknowledgement and PSA tests keep passing, changed only where a signature they call changed (Tasks 5 and 6 name them).
- Alert and rule identifiers stay strings as in `AlertTypes` (never renamed). A default pack rule's `DefaultKey` equals its kind's key.
- Severity stays the two values in use today (`"Info"`, `"Warning"`). Critical is Phase 2.
- The master switch `NotificationSettings.Enabled` still stops everything, checked before rules.
- A system alert kind (API key expiring, client report failed, PSA connection lost) has no scope: its rules match every time.
- `ConfigureAwait(false)` on awaits in library code, `AsNoTracking()` for read-only queries, as the surrounding code does.
- Migration is created with `dotnet ef migrations add AddAlertRules --project src/DotMarc` (never hand-written), and the test project must still apply it from scratch.
- Variable names are meaningful words (no `r`, `m`, `x` for non-trivial values), even in tests.
- Commit message style: short imperative sentence, no prefix.

## Review Focus

- **Upgrade with open alerts:** an alert open before the upgrade has no rule. After seeding it must belong to its type's default rule, so cooldown, resolve and acknowledge still find it and no duplicate is raised. Tasks 3 and 4.
- **Two instances starting at once:** both seed the default pack; the unique `DefaultKey` index means the loser must read the winner's rows, not crash or duplicate. Task 3.
- **A rule that stops applying:** disabling a rule, rescoping it away from a domain, or putting a stopping rule above it must resolve that rule's open alerts for that domain, and close their PSA tickets, within one cycle. Task 4.
- **Stacked rules on the same condition:** two rules that both match raise two alerts (one per rule), each with its own cooldown, and resolving one does not resolve the other. Task 4.
- **A legacy switch still wins:** turning New sender alerts off on the Alert settings page silences the default rule and a stacked rule of that kind alike. Tasks 2 and 4.
- **No rule applies:** a kind whose every rule is disabled or out of scope raises nothing and resolves open alerts, and does not throw. Tasks 3 and 4.

---

### Task 1: Rule tables and the migration

**Files:**
- Create: `src/DotMarc/Notifications/AlertRule.cs`
- Modify: `src/DotMarc/Notifications/AlertEvent.cs` (location of `AlertEvent`; add `AlertRuleId`), `src/DotMarc/Data/DotMarcDbContext.cs`
- Create (generated): `src/DotMarc/Migrations/*_AddAlertRules.cs`, `*.Designer.cs`, update `DotMarcDbContextModelSnapshot.cs`
- Test: `test/DotMarc.Tests/Notifications/AlertRuleModelTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  namespace DotMarc.Notifications;
  public enum AlertRuleScopeTarget { Group, Tag, Domain }
  public sealed class AlertRuleScopeEntry
  {
      public int Id { get; set; }
      public int AlertRuleId { get; set; }
      public AlertRuleScopeTarget Target { get; set; }
      public int TargetId { get; set; }
      public bool Exclude { get; set; }
  }
  public sealed class AlertRule
  {
      public int Id { get; set; }
      public required string Kind { get; set; }          // an AlertTypes key
      public required string Name { get; set; }
      public bool Enabled { get; set; } = true;
      public required string Severity { get; set; }       // "Info" or "Warning"
      public int Position { get; set; }                   // processing order within its kind, lowest first
      public bool StopProcessing { get; set; } = true;
      public string? DefaultKey { get; set; }              // set only on default pack rules; equals Kind
      public int? CooldownMinutes { get; set; }            // null uses the global cooldown
      public int? AutoCloseDays { get; set; }              // null uses the global setting where the kind has one
      public string? ParametersJson { get; set; }          // {"key":"value"} overrides; null means all inherited
      public List<AlertRuleScopeEntry> Scope { get; set; } = [];
  }
  ```
  `AlertEvent` gains `public int? AlertRuleId { get; set; }`.

- [ ] **Step 1: Write the failing test** `AlertRuleModelTests` (Postgres collection, migrate a fresh database as `AlertingServiceTests` does):
  - `ARule_WithScope_RoundTrips`: save an `AlertRule` (Kind `AlertTypes.MissedReport`, Severity `"Warning"`) with two scope entries (one include Group 5, one exclude Domain 9), reload with `.Include(rule => rule.Scope)`, assert the entries and defaults (`Enabled` true, `StopProcessing` true).
  - `DefaultKey_IsUnique`: saving two rules with `DefaultKey = "MissedReport"` throws `DbUpdateException`; two with `DefaultKey = null` are fine.
  - `DeletingARule_DeletesItsScope_AndClearsItsAlertsLink`: an `AlertEvent` with `AlertRuleId` set keeps existing (with `AlertRuleId` null) after its rule is deleted, and the scope entries are gone.
- [ ] **Step 2: Run to verify it fails**: `dotnet test test/DotMarc.Tests --filter AlertRuleModelTests`. Expected: compile error (types missing).
- [ ] **Step 3: Implement** the two classes and the `AlertEvent.AlertRuleId` property. In `OnModelCreating`: `AlertRule` (`Kind` max 100, `Name` max 200, `Severity` max 20, `DefaultKey` max 100, `ParametersJson` as text, unique index on `DefaultKey` (nulls distinct is PostgreSQL's default, so one index is enough), index on `(Kind, Position)`); `AlertRuleScopeEntry` (FK to `AlertRule` cascade, index `(AlertRuleId, Target, TargetId, Exclude)` unique); `AlertEvent` FK `AlertRuleId` to `AlertRule` with `OnDelete(DeleteBehavior.SetNull)` and an index on `(DomainName, AlertType, AlertRuleId)`. Add `DbSet<AlertRule> AlertRules` and `DbSet<AlertRuleScopeEntry> AlertRuleScopeEntries`.
- [ ] **Step 4: Generate the migration**: `dotnet ef migrations add AddAlertRules --project src/DotMarc`. Read it: it must create the two tables, add the column and indexes, and touch nothing else.
- [ ] **Step 5: Run to verify it passes**: the filter above. Expected: 3 passed.
- [ ] **Step 6: Commit**: `git add src test && git commit -m "Add alert rule tables"`.

---

### Task 2: Kinds catalogue and rule parameters with settings fallback

**Files:**
- Create: `src/DotMarc/Notifications/AlertRuleKinds.cs`, `src/DotMarc/Notifications/AlertRuleParameters.cs`
- Test: `test/DotMarc.Tests/Notifications/AlertRuleKindsTests.cs`

**Interfaces:**
- Consumes: `AlertTypes.All`, `NotificationSettings`, `DnsHealthAlertMode`.
- Produces:
  ```csharp
  public sealed record AlertRuleKind(string Key, string DisplayName, bool HasDomainScope, string DefaultSeverity);
  public static class AlertRuleKinds
  {
      public static IReadOnlyList<AlertRuleKind> All { get; }                 // one per AlertTypes.All entry, same order
      public static AlertRuleKind? Find(string key);
      /// The global switch that also turns a kind off: false when the kind's legacy setting disables it.
      public static bool IsEnabledBySettings(string kind, NotificationSettings settings);
  }
  public static class AlertRuleParameterKeys
  {
      public const string MissingDays = "days";                    // MissedReport
      public const string MinimumMessages = "minimumMessages";     // SuspiciousRejectActivity, NewUnreviewedSender, UnauthorisedSenderDelivered
      public const string NonBenignPercent = "nonBenignPercent";   // SuspiciousRejectActivity
      public const string NewDays = "newDays";                     // NewUnreviewedSender
      public const string Mode = "mode";                           // the seven DNS record kinds: a DnsHealthAlertMode name
  }
  public sealed class AlertRuleParameters(string? json)
  {
      public int Int(string key, int fallback);                    // fallback when unset, not a number, or null json
      public DnsHealthAlertMode Mode(DnsHealthAlertMode fallback); // fallback when unset or not an enum name
  }
  ```
  Kind facts (copy exactly): `HasDomainScope` is false for `ApiKeyExpiring`, `ClientReportFailed`, `PsaConnectionLost`, true for every other kind. `DefaultSeverity` is `"Info"` for `NameserversChanged`, `"Warning"` for all others. `IsEnabledBySettings`: `NewUnreviewedSender` follows `NewSenderAlertEnabled`; `UnauthorisedSenderDelivered` follows `UnauthorisedDeliveredAlertEnabled`; `DmarcPolicyWeakened` follows `DmarcPolicyWeakenedEnabled`; `NameserversChanged` follows `NameserversChangedEnabled`; each of the seven DNS record kinds is false when its mode in settings is `Off` (use `DnsHealthChecks.All` to find the mode accessor by alert type); everything else is true.

- [ ] **Step 1: Write the failing tests** `AlertRuleKindsTests` (no database):
  - `EveryAlertType_HasAKind_InTheSameOrder`: `AlertRuleKinds.All.Select(kind => kind.Key)` equals `AlertTypes.All.Select(type => type.Key)`.
  - `OnlyTheThreeSystemKinds_HaveNoDomainScope`.
  - `NameserversChanged_DefaultsToInfo_AndTheRestToWarning`.
  - `IsEnabledBySettings_FollowsEachLegacySwitch` (theory over the four switch kinds, each on and off).
  - `ADnsRecordKind_IsOffWhenItsSettingModeIsOff`: set `SpfAlertMode = DnsHealthAlertMode.Off`, assert `SpfRecordBroken` false, `DmarcRecordBroken` true.
  - `Parameters_Int_ReadsAnOverride_AndFallsBackWhenUnsetOrBad` (json `null`, `""`, `{"days":"5"}`, `{"days":"abc"}`, malformed json `{`, all fall back except the valid one).
  - `Parameters_Mode_ReadsAnEnumName_AndFallsBack` (`{"mode":"WheneverItFails"}`, `{"mode":"nope"}`).
- [ ] **Step 2: Run to verify it fails**: `dotnet test test/DotMarc.Tests --filter AlertRuleKindsTests`. Expected: compile errors.
- [ ] **Step 3: Implement** `AlertRuleKinds` (build `All` from `AlertTypes.All` so a new alert type is automatically a kind) and `AlertRuleParameters` (parse once with `System.Text.Json` into `Dictionary<string,string>`; any exception means empty).
- [ ] **Step 4: Run to verify it passes**. Expected: all passed.
- [ ] **Step 5: Commit**: `git commit -m "Describe the alert rule kinds and read rule parameters with a settings fallback"`.

---

### Task 3: The default pack, seeding and the rule set

**Files:**
- Create: `src/DotMarc/Notifications/AlertRuleSeeder.cs`, `src/DotMarc/Notifications/AlertRuleSet.cs`
- Test: `test/DotMarc.Tests/Notifications/AlertRuleSeederTests.cs`

**Interfaces:**
- Consumes: Task 1 entities, Task 2 `AlertRuleKinds`, `AlertRuleParameters`.
- Produces:
  ```csharp
  public static class AlertRuleSeeder
  {
      /// One rule per kind, DefaultKey = kind key, Position = index * 10, never overwrites an existing rule.
      public static IReadOnlyList<AlertRule> DefaultPack();
      /// Adds any missing default pack rule, then points alerts that have no rule at their type's default rule.
      public static Task EnsureDefaultPackAsync(DotMarcDbContext context, CancellationToken cancellationToken);
  }
  public sealed record DomainFacts(int DomainId, IReadOnlySet<int> GroupIds, IReadOnlySet<int> TagIds);
  public sealed record ResolvedAlertRule(AlertRule Rule, AlertRuleParameters Parameters)
  {
      public int RuleId => Rule.Id;
      public string Severity => Rule.Severity;
  }
  public sealed class AlertRuleSet
  {
      public AlertRuleSet(IReadOnlyList<AlertRule> rules, NotificationSettings settings);
      /// Ensures the default pack exists, then loads every rule with its scope.
      public static Task<AlertRuleSet> LoadAsync(DotMarcDbContext context, NotificationSettings settings, CancellationToken cancellationToken);
      /// Rules of a kind for one domain, in order, stopping after the first that has StopProcessing. facts is null for a system kind.
      public IReadOnlyList<ResolvedAlertRule> For(string kind, DomainFacts? facts);
      public AlertRule? Find(int ruleId);
      /// In-memory default pack with synthetic ids (1-based by position), for tests and callers that have no database.
      public static AlertRuleSet Defaults(NotificationSettings settings);
  }
  ```
  `For` skips a rule when `!rule.Enabled`, or `!AlertRuleKinds.IsEnabledBySettings(kind, settings)`, or its scope does not match. A scope matches when the kind has no domain scope; otherwise when there is no include entry or at least one include entry matches (`Group` in `GroupIds`, `Tag` in `TagIds`, `Domain` equal to `DomainId`), and no exclude entry matches. It then adds the rule, and stops if `StopProcessing`.
  Default names are the kind's `DisplayName`; severity the kind's `DefaultSeverity`; `StopProcessing` true; scope empty; `ParametersJson` null.

- [ ] **Step 1: Write the failing tests** (Postgres collection for the seeder ones; `For` tests build `AlertRuleSet` in memory):
  - `Seeding_CreatesOneDefaultRulePerKind_WithTheKindsNameAndSeverity`.
  - `Seeding_IsIdempotent`: run twice, still one per kind.
  - `Seeding_DoesNotOverwriteAnAdminsChanges`: change a default rule's name, severity and `Enabled`, seed again, unchanged.
  - `Seeding_AddsAMissingKindOnly`: delete the `SpfRecordBroken` default rule, seed, only that one is re-added.
  - `Seeding_PointsOpenAndResolvedAlertsAtTheirDefaultRule`: alerts of types `MissedReport` and `NewUnreviewedSender` with null `AlertRuleId` get those types' default rule ids; an alert already pointing at another rule keeps it; an alert whose type is not in the pack is left null.
  - `TwoSeedersAtOnce_EndWithOneRulePerKind`: run `EnsureDefaultPackAsync` concurrently on two contexts (`Task.WhenAll`), neither throws, one rule per kind.
  - `For_ReturnsTheDefaultRule_ForAnyDomain`.
  - `For_SkipsADisabledRule_AndALegacyDisabledOne` (rule disabled; `NewSenderAlertEnabled = false`).
  - `For_Scope_Include_MatchesByGroupTagOrDomain` and `For_Scope_Exclude_RemovesAMatch` and `For_Scope_ExcludeOverridesInclude`.
  - `For_StopsAfterARuleThatStops_AndContinuesPastOneThatDoesNot`: rules at positions 10 (stop), 20, 30: only the first returned; with the first not stopping, the first and second returned (the second stops).
  - `For_ASystemKind_IgnoresScope_AndNullFacts`.
  - `For_OrdersByPosition_ThenId`.
  - `For_ReturnsNothing_WhenEveryRuleIsOutOfScope`.
- [ ] **Step 2: Run to verify it fails**: `--filter AlertRuleSeederTests`. Expected: compile errors.
- [ ] **Step 3: Implement.** `EnsureDefaultPackAsync`: read the existing `DefaultKey`s, add the missing rules, `SaveChangesAsync` inside a try that catches `DbUpdateException` from the unique index (detach the failed entries and carry on, since another instance won), then for each default rule run `AlertEvents.Where(alert => alert.AlertRuleId == null && alert.AlertType == rule.Kind).ExecuteUpdateAsync(...)` setting `AlertRuleId`. `LoadAsync` calls it, then reads `AlertRules.AsNoTracking().Include(Scope)`.
- [ ] **Step 4: Run to verify it passes**. Expected: all passed.
- [ ] **Step 5: Commit**: `git commit -m "Seed a default pack of alert rules and resolve which rules apply to a domain"`.

---

### Task 4: Cycle alerts through rules, and alerts keyed by rule (missing report, reject activity, senders)

**Files:**
- Modify: `src/DotMarc/Notifications/AlertingService.cs` (`CheckPinnedDomainsAsync`, `LoadSenderAlertContextAsync`, `CheckSenderAlertsAsync`, `CheckSuspiciousRejectActivityAsync`)
- Test: `test/DotMarc.Tests/Notifications/AlertRuleAlertingTests.cs` (new), existing `AlertingServiceTests`, `SenderAlertingTests`, `SenderAlertSettingsTests` must pass unchanged.

**Interfaces:**
- Consumes: Task 3.
- Helpers to change first (private to `AlertingService`; the whole existing suite staying green proves them, and the new tests below drive them):
  - `EnsureAlertAsync(db, settings, ResolvedAlertRule rule, string subject, string title, string message, ct, TimeSpan? defaultCooldown = null)`: looks for an open alert with the same subject, type (`rule.Rule.Kind`) and `AlertRuleId`; cooldown is `rule.Rule.CooldownMinutes`, else `defaultCooldown`, else `settings.CooldownMinutes`; the new alert takes the rule's severity and id. Webhook and PSA ticket calls unchanged.
  - `ResolveAlertAsync(subject, kind, int? ruleId = null)` and `ResolveAllCopiesAsync(subject, kind, int? ruleId = null)`: a null rule id resolves across every rule (what the public `ResolveDomainAlertAsync`, `HandleTlsrptReportAsync` and the system resolves need); a rule id resolves only that rule's alerts.
  - `private sealed record OpenAlert(string Subject, string Kind, int? RuleId)`, `LoadOpenAlertsAsync(db, kinds, ct)` (one query per cycle), and `ResolveOrphanedAlertsAsync(db, openAlerts, applicable, ct)` where `applicable` is `Func<string subject, string kind, IReadOnlySet<int>>`: resolves every open alert with a non-null `AlertRuleId` outside the applicable set for its subject and kind, closing its PSA tickets.
  - A temporary `private static ResolvedAlertRule DefaultRule(string kind, NotificationSettings settings)` returning `AlertRuleSet.Defaults(settings).For(kind, null)[0]`, used by the call sites Tasks 5 and 6 have not converted yet. Task 6 deletes it.
- Behaviour to produce:
  - `CheckPinnedDomainsAsync` loads `AlertRuleSet.LoadAsync(db, settings, ct)` once, adds `.Include(domain => domain.Tags)` to the domain query, and builds `DomainFacts` per domain (`Groups` ids, `Tags` ids).
  - **MissedReport:** for each rule in `rules.For(AlertTypes.MissedReport, facts)`, `days = rule.Parameters.Int(AlertRuleParameterKeys.MissingDays, settings.MissingReportThresholdDays)`; the cutoff and the existing three-way branch (null-routed, fresh, stale) is evaluated per rule with that rule's cutoff, raising or resolving that rule's own alert. The null-routed `UnexpectedActivityOnNullRoutedDomain` resolution keeps using the default threshold from settings (it is not a missed report rule).
  - **SuspiciousRejectActivity:** per rule, `minimumMessages` (fallback `SuspiciousRejectMinVolume`) and `nonBenignPercent` (fallback `SuspiciousRejectNonBenignPercent`).
  - **Sender alerts:** per rule of each of the two kinds; `newDays` (fallback `NewSenderDays`, clamped 1 to 30) and `minimumMessages` (fallbacks `NewSenderMinVolume`, `UnauthorisedDeliveredMinVolume`). `LoadSenderAlertContextAsync` loads decisions and IP data when any rule of either kind exists for any domain, replacing the two `settings.*Enabled` tests. The candidate-new-IP query runs once per distinct `newDays` among the applicable rules for the domain.
  - After each domain's kinds are processed, call `ResolveOrphanedAlertsAsync` for the cycle kinds, so a rule that no longer applies has its alerts resolved. Use one `LoadOpenAlertsAsync` for the cycle.
  - A kind with no applicable rule for a domain resolves that domain's open alerts of the kind (all rules), exactly as a disabled setting does today.

- [ ] **Step 1: Write the failing tests** in `AlertRuleAlertingTests` (Postgres collection; helper to add rules directly to the context):
  - `ARuleScopedToAGroup_WithAHigherThreshold_ReplacesTheDefaultForThatGroupOnly`: default `days = 2`, a rule above it scoped to group "Aurora" with `{"days":"5"}`; a domain in the group last reported 3 days ago raises nothing, a domain outside the group reporting 3 days ago raises.
  - `ARuleThatDoesNotStop_RaisesItsOwnAlert_BesideTheDefault`: stacked rule `StopProcessing = false`, two alerts (two rule ids) for one domain, each with its own cooldown.
  - `ResolvingOneStackedRule_LeavesTheOther`.
  - `DisablingARule_ResolvesItsOpenAlerts_AndClosesItsTickets`.
  - `RescopingARuleAwayFromADomain_ResolvesThatDomainsAlert`.
  - `APutAboveStoppingRule_ResolvesTheShadowedRulesAlert`.
  - `ACooldownOverride_OnARule_IsHonoured` (rule cooldown 1 minute raises again after the minute in the test's controlled time; if time cannot be controlled, use a cooldown of 0 and assert two alerts across two cycles).
  - `EveryRuleOfAKindDisabled_RaisesNothing_AndResolvesOpenAlerts`.
  - `TheLegacySwitchStillSilencesAStackedRule`: `NewSenderAlertEnabled = false` silences both rules of that kind.
  - `ANewSenderRule_WithItsOwnNewDays_UsesIt` (one rule 7 days, a stacked one 1 day: a sender first seen 3 days ago is new for the first only).
  - `AnUpgradedOpenAlert_WithNoRule_IsAdoptedByTheDefaultRule_AndNotDuplicated`: insert an open `MissedReport` alert with null `AlertRuleId`, run a cycle, still one open alert for the domain, now with the default rule's id.
- [ ] **Step 2: Run to verify they fail**: `--filter AlertRuleAlertingTests`. Expected: failures (behaviour absent).
- [ ] **Step 3: Implement** as described. Keep the existing messages and titles byte for byte.
- [ ] **Step 4: Run the new and the existing alerting tests**: `--filter "FullyQualifiedName~Notifications"`. Expected: all pass, including the old suite unchanged.
- [ ] **Step 5: Commit**: `git commit -m "Raise the cycle alerts from rules"`.

---

### Task 5: DNS health through rules

**Files:**
- Modify: `src/DotMarc/Notifications/DnsHealthAlertEvaluator.cs`, `src/DotMarc/Notifications/DnsHealthChecks.cs` (remove the `Mode` accessor, keep it only if a test needs it), `src/DotMarc/Notifications/AlertingService.cs` (`CheckDnsHealthAsync`, `AutoCloseAcknowledgeableAlertsAsync`)
- Modify (signature only): `test/DotMarc.Tests/Notifications/DnsHealthAlertEvaluatorTests.cs`, `DnsHealthAlertingTests.cs`
- Test: `test/DotMarc.Tests/Notifications/DnsHealthRuleTests.cs`

**Interfaces:**
- Consumes: Tasks 3 and 4.
- Produces:
  ```csharp
  public sealed record DnsHealthAlertAction(DnsHealthActionKind Kind, string AlertType, int? RuleId = null, string Severity = "", string Title = "", string Message = "")
  {
      public static DnsHealthAlertAction Resolve(string alertType, int? ruleId = null) => ...;
  }
  public static IReadOnlyList<DnsHealthAlertAction> Evaluate(Domain domain, List<DomainAlertState> states, AlertRuleSet rules, NotificationSettings settings, DateTimeOffset nowUtc);
  ```
  (`Evaluate` replaces the `NotificationSettings`-only overload; `settings` is still needed for the legacy fallbacks. Tests that called the old overload now pass `AlertRuleSet.Defaults(settings)`.)
- Behaviour (per record check, with `applicable = rules.For(check.AlertType, facts)` where `facts` comes from the domain's `Id`, `Groups` and `Tags` ids):
  - Health passing: set `HasPassed`, clear pending, `Resolve(type)` (all rules). Health ignored: clear pending, `Resolve(type)`.
  - Failing, no applicable rule: clear pending, `Resolve(type)`.
  - Failing: each applicable rule wants an alert when its mode (`rule.Parameters.Mode(check mode from settings)`) is `WheneverItFails`, or `WhenItBreaks` with `state.HasPassed`. If none wants one: clear pending, `Resolve(type)` for each applicable rule. Otherwise run `IsConfirmed` once (the shared pending timer): when confirmed, `Raise(type, ruleId, rule.Severity, message)` for each wanting rule and `Resolve(type, ruleId)` for each applicable rule that does not want one. The message text is unchanged.
  - Policy weakened and nameservers changed: as today, but "enabled" means `rules.For(kind, facts)` is non-empty; raise once per applicable rule with its severity; the baseline and pending logic stay per domain.
  - `CheckDnsHealthAsync`: one `AlertRuleSet` for the cycle (shared with Task 4's), actions carry `RuleId`; `Raise` calls `EnsureAlertAsync` with the matching `ResolvedAlertRule` (look it up in the same applicable list); `Resolve` with a null rule id uses `ResolveAllCopiesAsync(domain, type)`, with an id only that rule; `ResolveOrphanedAlertsAsync` runs for the `AlertTypes.DnsHealth` kinds. The `IsStillPendingAsync` guard is unchanged.
  - `AutoCloseAcknowledgeableAlertsAsync`: an alert's days are its rule's `AutoCloseDays` when set, else `settings.AcknowledgeableAutoCloseDays`; 0 or less means never.

- [ ] **Step 1: Write the failing tests** `DnsHealthRuleTests`:
  - `ARuleWithModeWheneverItFails_AlertsForADomainThatNeverPassed_WhileTheDefaultStaysQuiet` (default rule `WhenItBreaks`, a rule above it scoped to one group with `{"mode":"WheneverItFails"}` and stop on: only the group's domain alerts).
  - `StackedRules_WithDifferentModes_RaiseOnlyForTheOneThatWantsIt`.
  - `TheSharedConfirmationDelay_StillApplies_WithRules` (first failing cycle raises nothing, a later cycle past `ConfirmationDelay` raises).
  - `ARuleSeverity_IsUsed_ForThePolicyWeakenedAlert` (rule severity `"Info"` produces an `Info` alert).
  - `NoApplicableRule_ForADnsKind_ResolvesItsAlertsAndClearsPending`.
  - `APolicyWeakenedRule_ScopedToAGroup_OnlyAlertsThatGroup`.
  - `AutoCloseDays_OnARule_OverridesTheGlobalSetting`: global 0, rule 1 day, alert 2 days old gets closed; global 30, rule `AutoCloseDays` null still uses 30.
  - Update `DnsHealthAlertEvaluatorTests` and `DnsHealthAlertingTests` call sites to the new `Evaluate` signature only; their assertions must not change.
- [ ] **Step 2: Run to verify they fail**: `--filter "FullyQualifiedName~DnsHealth"`. Expected: compile errors, then failures.
- [ ] **Step 3: Implement.** Keep `DnsHealthChecks.All`'s `Mode` accessor (Task 2 uses it for the legacy `Off`); the evaluator reads the legacy mode from it as the parameter fallback.
- [ ] **Step 4: Run** `--filter "FullyQualifiedName~DnsHealth|FullyQualifiedName~AlertAcknowledgement|FullyQualifiedName~AlertRule"`. Expected: all pass.
- [ ] **Step 5: Commit**: `git commit -m "Raise DNS health alerts from rules"`.

---

### Task 6: Event-driven and system alerts through rules

**Files:**
- Modify: `src/DotMarc/Notifications/AlertingService.cs` (`HandleTlsrptReportAsync`, `FlagUnexpectedActivityForNullRoutedDomainAsync`, `RaiseClientReportFailedAsync`, `RaisePsaConnectionLostAsync`, `CheckApiKeyExpiryAsync`; remove the Task 4 `DefaultRule` shim)
- Test: `test/DotMarc.Tests/Notifications/AlertRuleEventAlertingTests.cs`; `ClientReportAlertTests`, `PsaConnectionAlertTests`, `ApiKeyExpiryAlertingTests`, `AlertingServiceTests` must pass unchanged.

**Interfaces:**
- Consumes: Tasks 3 to 5.
- Behaviour: each of these loads `AlertRuleSet.LoadAsync` after the master switch check. The two domain kinds look up `DomainFacts` with a small query by domain name (`Domains.AsNoTracking().Where(domain => domain.Name == domainName)` projecting id, group ids, tag ids; an unknown name gives `new DomainFacts(0, empty, empty)`), then call `rules.For(kind, facts)` and raise once per rule. The three system kinds use `rules.For(kind, null)`. No applicable rule raises nothing. The API key kind keeps its 14-day default cooldown as `defaultCooldown` (a rule's own cooldown beats it). Resolution functions keep their null-rule-id behaviour. `CheckApiKeyExpiryAsync` also runs `ResolveOrphanedAlertsAsync` for its kind.

- [ ] **Step 1: Write the failing tests** `AlertRuleEventAlertingTests`:
  - `ATlsrptRule_ScopedToAGroup_OnlyAlertsThatGroupsDomains`.
  - `ADisabledTlsrptRule_RaisesNothing`.
  - `ANullRoutedActivityRule_UsesItsOwnSeverity`.
  - `ASystemKindRule_Disabled_RaisesNothing` (theory over `ApiKeyExpiring`, `ClientReportFailed`, `PsaConnectionLost`).
  - `TheApiKeyAlert_KeepsItsFourteenDayCooldown_ByDefault_AndARuleCooldownOverridesIt`.
  - `AClientReportFailedRule_SeverityAppearsOnTheAlert`.
- [ ] **Step 2: Run to verify they fail**: `--filter AlertRuleEventAlertingTests`. Expected: failures.
- [ ] **Step 3: Implement** and delete the `DefaultRule` shim; the build must have no remaining uses.
- [ ] **Step 4: Run** `--filter "FullyQualifiedName~Notifications"`. Expected: all pass.
- [ ] **Step 5: Commit**: `git commit -m "Raise the event-driven and system alerts from rules"`.

---

### Task 7: Demo, spec note, and the full suite

**Files:**
- Modify: `src/DotMarc/Demo/DemoDataSeeder.cs` (nothing to seed: the default pack seeds itself on first use; this task only verifies it and adds one demo rule), `docs/superpowers/specs/2026-11-19-alert-rules-design.md`
- Test: `test/DotMarc.Tests/Demo/DemoSenderStatesTests.cs` or a new `DemoAlertRulesTests.cs`

- [ ] **Step 1: Write the failing test** `TheDemoHasOneScopedAlertRule_BesideTheDefaultPack`: after seeding the demo, `AlertRules` contains the default pack (18) plus one extra rule: a Missing DMARC report rule scoped to the group "Aurora Retail" with `{"days":"5"}`, `StopProcessing` true, positioned above the default. (Seed it in `DemoDataSeeder` after `EnsureDefaultPackAsync`, finding the group by name from the seeded groups.)
- [ ] **Step 2: Run to verify it fails**, implement the seeding, run to verify it passes.
- [ ] **Step 3: Amend the spec** with the Phase 1 decision: in "Migration", add that Phase 1 seeds the default pack with no values of its own and a parameter a rule does not set falls back to the global setting, with the settings page unchanged; Phase 2 copies the values into the rules and retires the fields. Seeding happens the first time the rules are loaded (and backfills alerts), not in the migration.
- [ ] **Step 4: Run the whole suite**: `dotnet test dotMARC.sln`. Expected: 0 failed. The build must be warning free (warnings are errors).
- [ ] **Step 5: Commit**: `git add -A src test docs && git commit -m "Seed a demo alert rule and note how phase 1 falls back to the settings"`.
