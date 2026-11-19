# Alert Rules Phase 2 (Editor, Permission, Audit, Under Review) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An administrator can see, add, change, scope, order and remove alert rules from the app, behind a new Manage alert rules permission, with every change audited, an Under review setting on the new sender rule, a Critical severity, and the old threshold fields on the Alert settings page retired into the rules.

**Architecture:** Each rule kind describes its parameters in code (`AlertRuleParameterDefinition`: key, label, help, type, range, default), and one validator, one editor dialog and the engine all read that description. The default pack stops inheriting from the global settings: it is created, and for installs that ran Phase 1 upgraded once, with the setting values copied into explicit rule parameters, after which the engine reads only rules. `AlertRuleService` is the audited, validated write path (create, update, delete, move, reset). A page at `/alerts/rules` lists rules by kind in processing order and edits them in a dialog; pure helpers work out scope wording, how many domains a rule applies to and which rule hides which. The sender alert evaluator learns an Under review handling (counts as decided, counts as unreviewed, or alert again after N days).

**Tech Stack:** .NET 10, Blazor Server + MudBlazor 9, EF Core + Npgsql (no migration: the retired settings columns stay), xUnit with the Postgres fixture.

**Spec:** `docs/superpowers/specs/2026-11-19-alert-rules-design.md` (sections "What a rule is", "Which rule applies", "The editor", "Permissions and audit", "Phases" item 2). Builds on `docs/superpowers/plans/2026-11-19-alert-rules-phase-1.md`.

## Global Constraints

- No em dashes in any copy, comments or docs.
- Severities become `Info`, `Warning`, `Critical`, defined once in `AlertSeverities`. Existing alerts keep theirs.
- New permission `AlertRulesManage`, appended last in `Permission`. The Admin role gets it automatically (it receives every permission); custom roles do not. Viewing rules needs `AlertsView`.
- Every mutating `AlertRuleService` method takes `(DotMarcDbContext context, AuditActor actor, ...)` first, and `AlertRuleService` is added to `AuditCoverageTests.AuditedServices`.
- A default pack rule cannot be deleted, cannot change its kind, and offers Reset to default. A system kind (API key expiring, client report failed, PSA connection lost) has no scope.
- Parameter values are validated against their definition's range on every write, and the message names the parameter.
- After Task 5 the engine reads rules only. The retired `NotificationSettings` fields stay as columns, are no longer read by alerting, and are not shown on the Alert settings page. The master switch, channels, webhook URLs, cooldown and monitor interval stay.
- UI copy is sentence case, plain words, and uses the existing patterns (`FieldWithHelp`, `DocsLink`, `AuthorizeView`). Text that is secondary uses the `mud-text-secondary` class (never `Color.Secondary`), so it stays readable in dark mode.
- Variable names are meaningful words. Commit message style: short imperative sentence, no prefix. Warnings are build errors.

## Review Focus

- **Upgrade from Phase 1 with customised settings:** an install whose Phase 1 default rules still have no parameters gets the current setting values copied in once. An administrator's later edits are never overwritten on the next load, and a legacy switch that was off becomes a rule that is off. Task 5.
- **Under review boundaries:** a decision made exactly N days ago, one made N days minus a second ago, a decision inherited from a group or the baseline (its own decided date counts), a per-IP decision, and a sender re-decided after it alerted again. Task 4.
- **Invalid writes:** an out-of-range parameter, an unknown parameter, a scope on a system kind, a blank or over-long name, an unknown severity, deleting a default rule and changing a rule's kind are all refused with a message that names the problem, and change nothing. Task 6.
- **Reordering:** moving the first rule up or the last down does nothing; positions stay unique within the kind; moving never touches another kind. Task 6.
- **Dead scope targets:** a rule scoped to a group that has since been deleted shows that and can be cleaned, instead of silently matching nothing forever. Tasks 6 and 7.
- **A person without the permission:** sees the rules but no add, edit, delete, move or reset control, and the service is not reachable from the page. Task 8.

---

### Task 1: The Manage alert rules permission

**Files:**
- Modify: `src/DotMarc/Data/Permission.cs`, `website/docs/permissions-and-access.mdx`
- Test: `test/DotMarc.Tests/Data/AlertRulesPermissionTests.cs`

**Interfaces:**
- Produces: `Permission.AlertRulesManage` (appended after `SenderReview`).

- [ ] **Step 1: Write the failing test** `AlertRulesPermissionTests` (Postgres collection; follow `SenderDecisionServiceTests` line ~392 for how the Admin role is checked after bootstrapping):
  - `TheAdminRole_HasAlertRulesManage_AfterBootstrapping`.
  - `ACustomRole_DoesNotGetItAutomatically`: create a role with `[Permission.AlertsView, Permission.AlertsManage]`, run the bootstrapper again, the role still lacks `AlertRulesManage`.
  - `ThePermissionHasAnAuthorizationPolicy`: the app's `AuthorizationOptions` contains a policy named `AlertRulesManage` (resolve `IAuthorizationPolicyProvider` from `ApiTestHost`'s services, or build the options the way `Program.cs` does if there is a helper).
- [ ] **Step 2: Run to verify it fails**: `dotnet test test/DotMarc.Tests --filter AlertRulesPermissionTests`. Expected: compile error.
- [ ] **Step 3: Implement**: add the enum member. `Program.cs`, `ApiPolicies` and `AccessBootstrapper` already enumerate the enum, so no other code changes. In `permissions-and-access.mdx`, add `AlertRulesManage` to the list of independently grantable permissions with a one-line description: creating, changing, ordering and deleting alert rules, with the note that Admin has it and custom roles must be granted it.
- [ ] **Step 4: Run to verify it passes**. Expected: 3 passed.
- [ ] **Step 5: Commit**: `git commit -m "Add the Manage alert rules permission"`.

---

### Task 2: Parameter definitions and the validator

**Files:**
- Modify: `src/DotMarc/Notifications/AlertRuleKinds.cs`
- Create: `src/DotMarc/Notifications/AlertSeverities.cs`, `src/DotMarc/Notifications/AlertRuleValidator.cs`
- Test: `test/DotMarc.Tests/Notifications/AlertRuleParameterDefinitionTests.cs`, `AlertRuleValidatorTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static class AlertSeverities { public const string Info = "Info", Warning = "Warning", Critical = "Critical"; public static IReadOnlyList<string> All { get; } /* Info, Warning, Critical */ }
  public enum AlertRuleParameterType { Integer, Choice }
  public sealed record AlertRuleChoice(string Value, string Label);
  public sealed record AlertRuleParameterDefinition(
      string Key, string Label, string Help, AlertRuleParameterType Type, string Default,
      int? Min = null, int? Max = null, IReadOnlyList<AlertRuleChoice>? Choices = null,
      string? ShowWhenKey = null, string? ShowWhenValue = null);
  // AlertRuleKind gains: IReadOnlyList<AlertRuleParameterDefinition> Parameters
  // AlertRuleKinds gains:
  public static string DefaultOf(string kind, string parameterKey);          // the definition's default; throws ArgumentException for an unknown pair
  public static int DefaultInt(string kind, string parameterKey);
  public sealed record AlertRuleScopeInput(AlertRuleScopeTarget Target, int TargetId, bool Exclude);
  public sealed record AlertRuleInput(string Name, bool Enabled, string Severity, bool StopProcessing, int? CooldownMinutes, int? AutoCloseDays,
      IReadOnlyDictionary<string, string> Parameters, IReadOnlyList<AlertRuleScopeInput> Scope);
  public static class AlertRuleValidator { public static IReadOnlyList<string> Problems(string kind, AlertRuleInput input); }
  ```
  Definitions (labels and help are plain sentences; defaults and ranges exact): Missing DMARC report: `days` Integer default `2`, 1 to 90. Suspicious reject activity: `minimumMessages` `10`, 1 to 1000000; `nonBenignPercent` `50`, 1 to 100. Each of the seven DNS record kinds: `mode` Choice `WhenItBreaks` (labels "When it breaks", "Whenever it fails"). New sender to review: `newDays` `7`, 1 to 30; `minimumMessages` `20`, 1 to 1000000; `underReview` Choice `decided` ("Counts as decided"), `unreviewed` ("Counts as unreviewed"), `afterDays` ("Counts as decided for a while, then alert again"), default `decided`; `underReviewDays` Integer `14`, 1 to 365, `ShowWhenKey = "underReview"`, `ShowWhenValue = "afterDays"`. Unauthorised sender delivered: `minimumMessages` `10`, 1 to 1000000. Every other kind: none.
  Validator problems (each a sentence naming the field): name blank or over 200 characters; severity not in `AlertSeverities.All`; cooldown outside 0 to 10080 when set; auto-close outside 0 to 365 when set, or set on a kind that has no auto-close (only `DmarcPolicyWeakened` and `NameserversChanged` do); a parameter key the kind does not define; a parameter value that is not an integer in range or not one of the choices; a missing parameter is allowed (it falls back to the default); scope entries on a system kind; a scope target id of 0 or less; the same scope entry twice.

- [ ] **Step 1: Write the failing tests.** `AlertRuleParameterDefinitionTests`: `EveryKindsParametersAreDefinedAsTheSpecSays` (a theory over the table above), `EveryDefaultIsInsideItsOwnRange`, `EveryChoiceDefaultIsOneOfItsChoices`, `DefaultOf_ThrowsForAnUnknownPair`, `TheSettingsFallbackDefaults_MatchTheNotificationSettingsDefaults` (compare each definition default with the property default on `new NotificationSettings()`: `MissingReportThresholdDays`, `SuspiciousRejectMinVolume`, `SuspiciousRejectNonBenignPercent`, `NewSenderDays`, `NewSenderMinVolume`, `UnauthorisedDeliveredMinVolume`, and the DNS modes). `AlertRuleValidatorTests`: one test per problem listed above (each asserts the message contains the field's label), plus `AValidInput_HasNoProblems` and `SeveralProblems_AreAllReported`.
- [ ] **Step 2: Run to verify they fail**: `--filter "AlertRuleParameterDefinitionTests|AlertRuleValidatorTests"`. Expected: compile errors.
- [ ] **Step 3: Implement** the records, the definitions inside `AlertRuleKinds`, `AlertSeverities`, and the validator. `AlertRuleKind.Parameters` must default to empty for kinds without any.
- [ ] **Step 4: Run to verify they pass**. Expected: all passed.
- [ ] **Step 5: Commit**: `git commit -m "Describe each alert rule kind's parameters and validate rules against them"`.

---

### Task 3: Critical severity, end to end

**Files:**
- Modify: `src/DotMarc/Api/AlertEndpoints.cs` (the `Severities` list), `src/DotMarc/Components/Pages/Alerts.razor` (`GetSeverityColor`), `src/DotMarc/Notifications/AlertWebhookClient.cs` and `GenericWebhookClient.cs`, `src/DotMarc/Notifications/AlertingService.cs` (pass the alert's severity), `src/DotMarc/Notifications/AlertRuleSeeder.cs` and `AlertRuleKinds.cs` (use `AlertSeverities` constants), `website/data/openapi/dotmarc-api.json` (regenerated)
- Modify (signature): `test/DotMarc.Tests/Internal/FakeAlertWebhookClient.cs`, and the tests that call `SendAlertAsync`
- Test: `test/DotMarc.Tests/Notifications/AlertSeverityTests.cs`, `AlertWebhookClientTests` (extend), `test/DotMarc.Tests/Api/AlertEndpointTests.cs` (extend)

**Interfaces:**
- Produces: `IAlertWebhookClient.SendAlertAsync(NotificationSettings settings, string domainName, string alertType, string severity, string title, string message, CancellationToken cancellationToken = default)` and `IGenericWebhookClient.SendAlertAsync(string webhookUrl, string domainName, string alertType, string severity, string title, string message, CancellationToken cancellationToken = default)`. The generic payload's `severity` is the alert's, not a constant. Teams and Slack messages are unchanged except that a `Critical` alert's title is prefixed `Critical: `.
- The API's `severity` filter accepts `Info`, `Warning`, `Critical` (the existing `Severities` list becomes `AlertSeverities.All`).

- [ ] **Step 1: Write the failing tests:** `TheGenericWebhookPayload_CarriesTheAlertsSeverity` (an Info and a Critical alert, read the posted JSON), `ACriticalAlert_IsPrefixedInTeamsAndSlack`, `TheAlertsApi_AcceptsACriticalSeverityFilter` (an alert with `Critical` is returned for `severity=Critical`, and `severity=Bogus` is still refused naming the allowed values), `AnAlertRaisedByACriticalRule_IsStoredAsCritical_AndSentAsCritical` (Postgres: a stacked rule with severity Critical, `FakeAlertWebhookClient` records the severity).
- [ ] **Step 2: Run to verify they fail**. Expected: compile errors (severity parameter).
- [ ] **Step 3: Implement.** Thread `rule.Severity` from `EnsureAlertCoreAsync` into `SendAlertAsync`. Update `FakeAlertWebhookClient` to record the severity (`Sent` becomes `(DomainName, AlertType, Severity)`; fix the call sites in tests that destructure it). `GetSeverityColor` maps `Critical` to `Color.Error`, `Warning` to `Color.Warning`, `Info` to `Color.Info`. Regenerate the OpenAPI copy with `node scripts/update-openapi.mjs` and commit the change (the enum in the document gains `Critical`).
- [ ] **Step 4: Run** `--filter "FullyQualifiedName~Notifications|FullyQualifiedName~Api"`. Expected: all pass.
- [ ] **Step 5: Commit**: `git commit -m "Add a Critical alert severity and send each alert's own severity to webhooks"`.

---

### Task 4: Under review handling on the new sender rule

**Files:**
- Modify: `src/DotMarc/Reporting/Senders/SenderAlertEvaluator.cs`, `src/DotMarc/Notifications/AlertingService.cs` (`CheckSenderAlertsAsync`), `src/DotMarc/Notifications/AlertRuleParameters.cs`
- Test: `test/DotMarc.Tests/Reporting/SenderAlertEvaluatorUnderReviewTests.cs`, `test/DotMarc.Tests/Notifications/UnderReviewAlertingTests.cs`

**Interfaces:**
- Consumes: Task 2's `underReview` and `underReviewDays` definitions, `SenderDecision.DecidedUtc`, `ResolvedSenderDecision`.
- Produces:
  ```csharp
  public enum UnderReviewMode { CountsAsDecided, CountsAsUnreviewed, AlertAgainAfterDays }
  public sealed record UnderReviewHandling(UnderReviewMode Mode, int Days = 0)
  {
      public static UnderReviewHandling Default => new(UnderReviewMode.CountsAsDecided);
      /// Whether a sender with this resolved decision still needs an alert: no decision always does; Under review does per the mode.
      public bool NeedsAlert(ResolvedSenderDecision decision, DateTimeOffset nowUtc);
  }
  // SenderAlertInputs gains: UnderReviewHandling? UnderReview = null (null means Default), DateTimeOffset? NowUtc = null (null means UtcNow)
  // AlertRuleParameters gains: public UnderReviewHandling UnderReview(); // reads "underReview" and "underReviewDays", defaults decided
  ```
  `NeedsAlert`: `Status is null` is true; `Status == UnderReview` is false for `CountsAsDecided`, true for `CountsAsUnreviewed`, and for `AlertAgainAfterDays` true when `decision.Decision.DecidedUtc <= nowUtc - Days days` (so exactly N days counts); `Authorised` and `Unauthorised` are false. `SenderAlertEvaluator.Evaluate` uses it for both service rows and unrecognised IPs wherever it tests `Status is null` for the new sender list. `CheckSenderAlertsAsync` passes each new sender rule's handling, and the result memo key gains the handling so two rules with different handling do not share a result.

- [ ] **Step 1: Write the failing tests** `SenderAlertEvaluatorUnderReviewTests` (pure; build decisions with `DecidedUtc`): `CountsAsDecided_IsTodaysBehaviour`, `CountsAsUnreviewed_AlertsForAnUnderReviewService`, `AfterDays_DoesNotAlertBeforeTheWindow` (decided N days minus one second ago), `AfterDays_AlertsExactlyAtTheWindow` (exactly N days ago), `AfterDays_UsesTheInheritedDecisionsOwnDate` (a group decision made long ago applies to a domain with none of its own), `AfterDays_WorksForAnUnrecognisedIp`, `AnAuthorisedSender_NeverAlerts_WhateverTheMode`, `AReDecidedSender_StopsAlertingAgain` (Under review then changed to Authorised). `UnderReviewAlertingTests` (Postgres, through the cycle with a stacked rule): `ARuleWithAfterDays_RaisesAgainOnceTheWindowPasses_WhileTheDefaultStaysQuiet`.
- [ ] **Step 2: Run to verify they fail**: `--filter "SenderAlertEvaluatorUnderReviewTests|UnderReviewAlertingTests"`. Expected: compile errors.
- [ ] **Step 3: Implement** as specified. Keep the default behaviour byte for byte when no handling is passed.
- [ ] **Step 4: Run** `--filter "FullyQualifiedName~Sender|FullyQualifiedName~UnderReview|FullyQualifiedName~Notifications"`. Expected: all pass, including every existing sender alert test unchanged.
- [ ] **Step 5: Commit**: `git commit -m "Let a new sender rule decide what Under review means"`.

---

### Task 5: The default pack carries its own values; the engine reads rules only

**Files:**
- Modify: `src/DotMarc/Notifications/AlertRuleSeeder.cs`, `AlertRuleSet.cs`, `AlertRuleParameters.cs`, `AlertRuleKinds.cs`, `AlertingService.cs`, `DnsHealthAlertEvaluator.cs`, `DnsHealthChecks.cs`, `NotificationSettings.cs` (doc comments only)
- Modify (mechanical): tests that build `AlertRuleSet(rules, settings)`, call `AlertRuleSet.Defaults(settings)`, `IsEnabledBySettings`, `rule.Parameters.Int(key, fallback)`, `DnsHealthAlertEvaluator.Evaluate(..., settings, ...)`; and any test that changed a retired setting after the first cycle (it now edits the default rule through a helper)
- Test: `test/DotMarc.Tests/Notifications/AlertRuleMaterialisationTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // AlertRuleSeeder
  public static List<AlertRule> DefaultPack(NotificationSettings settings);        // explicit ParametersJson from the settings, Enabled from the legacy switches, AutoCloseDays from AcknowledgeableAutoCloseDays (0 becomes null)
  public static Task EnsureDefaultPackAsync(DotMarcDbContext context, CancellationToken cancellationToken);  // reads the settings row itself; creates missing default rules from it; then materialises (below)
  // Materialise: every default rule whose ParametersJson is null gets the values from the settings row once (parameters, Enabled &&= legacy switch, AutoCloseDays), and is never touched again.
  // AlertRuleSet
  public AlertRuleSet(IReadOnlyList<AlertRule> rules);                              // no settings: nothing reads the retired fields
  public static AlertRuleSet Defaults();                                            // in-memory pack from default NotificationSettings, ids 1 and up
  // ResolvedAlertRule
  public int Int(string key);                                                       // the rule's value, else AlertRuleKinds.DefaultInt(Rule.Kind, key)
  public DnsHealthAlertMode Mode();                                                 // the rule's "mode", else WhenItBreaks
  public UnderReviewHandling UnderReview();
  // Removed: AlertRuleKinds.IsEnabledBySettings, the settings parameter of AlertRuleSet and of DnsHealthAlertEvaluator.Evaluate.
  ```
  `AlertingService` and `DnsHealthAlertEvaluator` read `rule.Int(AlertRuleParameterKeys.X)`, `rule.Mode()`, `rule.UnderReview()` and `rule.Rule.AutoCloseDays` (null or 0 means never). Cooldown stays `rule.CooldownMinutes ?? settings.CooldownMinutes`.

- [ ] **Step 1: Write the failing tests** `AlertRuleMaterialisationTests` (Postgres): `ANewInstall_CreatesDefaultRulesWithExplicitDefaultValues` (every default rule that has parameters has all of them in `ParametersJson`, equal to the definition defaults), `CustomisedSettings_AreCopiedIntoTheRulesOnCreation` (settings with `MissingReportThresholdDays = 5`, `NewSenderDays = 3`, `SpfAlertMode = WheneverItFails`, `NewSenderAlertEnabled = false`, `AcknowledgeableAutoCloseDays = 9` produce rules with 5, 3, `WheneverItFails`, `Enabled == false`, auto-close 9), `APhaseOneInstall_GetsItsValuesCopiedOnce` (create default rules with null parameters as Phase 1 left them, set customised settings, load rules: parameters now explicit), `ALaterEditToARule_IsNeverOverwrittenByTheSettings` (after materialising, change the rule and the settings, load again: the rule is unchanged), `ADisabledRuleStaysDisabled_WhenItsSettingIsOn`, `TheEngine_NoLongerReadsTheRetiredSettings` (change `MissingReportThresholdDays` after the pack exists, run a cycle: the rule's value still decides).
- [ ] **Step 2: Run to verify they fail**. Expected: compile errors, then failures.
- [ ] **Step 3: Implement** the seeder, the rule set and the engine changes above. The materialise step runs inside `EnsureDefaultPackAsync` after the create step, and is one query when nothing needs it (a count of default rules with null parameters).
- [ ] **Step 4: Fix the tests** that no longer compile or that changed a retired setting after the pack existed: introduce `Task EditDefaultRuleAsync(string kind, Action<AlertRule> change)` as a shared test helper in `test/DotMarc.Tests/Internal/AlertRuleTestSupport.cs` and use it. Assertions must not change, only how the scenario is set up. Name each file you changed in the ledger.
- [ ] **Step 5: Run** `--filter "FullyQualifiedName~Notifications|FullyQualifiedName~Demo"`. Expected: all pass.
- [ ] **Step 6: Commit**: `git commit -m "Copy the alert settings into the default rules and stop reading them"`.

---

### Task 6: The audited rule service

**Files:**
- Create: `src/DotMarc/Notifications/AlertRuleService.cs`
- Modify: `src/DotMarc/Audit/AuditActions.cs`, `src/DotMarc/Audit/AuditTarget.cs`, `test/DotMarc.Tests/Audit/AuditCoverageTests.cs`
- Test: `test/DotMarc.Tests/Notifications/AlertRuleServiceTests.cs`

**Interfaces:**
- Consumes: Task 2's `AlertRuleInput`, `AlertRuleValidator`, `AlertSeverities`; Task 5's `AlertRuleSeeder`.
- Produces:
  ```csharp
  public sealed class AlertRuleValidationException(IReadOnlyList<string> problems) : Exception(string.Join(" ", problems)) { public IReadOnlyList<string> Problems { get; } }
  public static class AlertRuleService
  {
      public static Task<AlertRule> CreateAsync(DotMarcDbContext context, AuditActor actor, string kind, AlertRuleInput input, CancellationToken cancellationToken = default);
      public static Task UpdateAsync(DotMarcDbContext context, AuditActor actor, int ruleId, AlertRuleInput input, CancellationToken cancellationToken = default);
      public static Task DeleteAsync(DotMarcDbContext context, AuditActor actor, int ruleId, CancellationToken cancellationToken = default);
      public static Task MoveAsync(DotMarcDbContext context, AuditActor actor, int ruleId, int direction, CancellationToken cancellationToken = default); // -1 up, +1 down
      public static Task ResetToDefaultAsync(DotMarcDbContext context, AuditActor actor, int ruleId, CancellationToken cancellationToken = default);
      public static Task<int> PruneDeadScopeAsync(DotMarcDbContext context, AuditActor actor, CancellationToken cancellationToken = default); // removes scope entries whose group, tag or domain no longer exists; returns how many
      public static Task<IReadOnlyList<AlertRuleScopeEntry>> ListDeadScopeAsync(DotMarcDbContext context, CancellationToken cancellationToken = default); // a read: the coverage test treats the List prefix as non-mutating
  }
  ```
  Behaviour: `CreateAsync` validates, then inserts the rule one position above the lowest position of its kind (so it sits first), `StopProcessing` as given (the dialog defaults it on). `UpdateAsync` replaces name, enabled, severity, stop, cooldown, auto-close, parameters (stored as a JSON object of the given values, every defined parameter written so the rule is explicit) and scope; it never changes `Kind` or `DefaultKey`. `DeleteAsync` refuses a rule with a `DefaultKey`. `MoveAsync` swaps `Position` with the neighbour of the same kind (renumbering the kind's rules 10, 20, 30... if two share a position), and does nothing at either end. `ResetToDefaultAsync` is for default rules only and restores name, severity (kind default), enabled true, stop true, no cooldown, no auto-close, no scope, and the definition defaults. Alerts a rule raised keep their `AlertRuleId` (deleting a rule nulls it, as the table's rule says). Audit: `AlertRuleCreated`, `AlertRuleChanged`, `AlertRuleDeleted`, `AlertRuleMoved`, `AlertRuleReset`, `AlertRuleScopePruned` with labels, target `AuditTarget.For(AlertRule)` (`new("AlertRule", id, name)`), and field-level `AuditChanges` for every field of an update (old and new value; scope as text).

- [ ] **Step 1: Write the failing tests** `AlertRuleServiceTests` (Postgres; default pack present from `EnsureDefaultPackAsync`): `Create_PutsTheRuleFirstInItsKind_AndAuditsIt`, `Create_RefusesAnInvalidRule_AndStoresNothing` (theory over the validator's problems plus an unknown kind), `Create_StoresEveryDefinedParameter_ExplicitlyWithDefaultsForTheOnesGiven`, `Update_ChangesTheFields_AuditingEachOldAndNewValue`, `Update_CannotChangeTheKindOrDefaultKey`, `Update_RefusesAnOutOfRangeParameter_NamingIt`, `Delete_RemovesACustomRule_AndKeepsItsAlerts`, `Delete_RefusesADefaultRule`, `Move_SwapsWithTheNeighbour`, `Move_AtTheEnds_DoesNothing`, `Move_NeverTouchesAnotherKind`, `Move_FixesDuplicatePositions`, `Reset_RestoresADefaultRulesShippedValues`, `Reset_RefusesACustomRule`, `PruneDeadScope_RemovesEntriesForDeletedGroupsTagsAndDomains_AndAuditsTheCount`, `ListDeadScope_FindsThemWithoutChangingAnything`, `EveryMethodRecordsItsActor` (the audit entry's actor is the one passed in). Add `typeof(AlertRuleService)` to `AuditCoverageTests.AuditedServices` and make sure it passes.
- [ ] **Step 2: Run to verify they fail**: `--filter "AlertRuleServiceTests|AuditCoverageTests"`. Expected: compile errors.
- [ ] **Step 3: Implement** the service, the audit actions (constants in `AuditActions`, labels in its label list), `AuditTarget.For(AlertRule)`. Use a single transaction per call (`SaveChangesAsync` once).
- [ ] **Step 4: Run** `--filter "AlertRuleServiceTests|AuditCoverageTests|FullyQualifiedName~Audit"`. Expected: all pass (the audit tests enumerate `AuditActions`, so a missing label fails them).
- [ ] **Step 5: Commit**: `git commit -m "Add an audited service for creating, changing, ordering and deleting alert rules"`.

---

### Task 7: Rule presentation helpers (scope wording, reach, shadowing)

**Files:**
- Create: `src/DotMarc/Notifications/AlertRulePresentation.cs`
- Test: `test/DotMarc.Tests/Notifications/AlertRulePresentationTests.cs`

**Interfaces:**
- Consumes: `AlertRule`, `AlertRuleScopeEntry`, `DomainFacts`, `AlertRuleKinds`.
- Produces:
  ```csharp
  public sealed record AlertScopeNames(IReadOnlyDictionary<int, string> Groups, IReadOnlyDictionary<int, string> Tags, IReadOnlyDictionary<int, string> Domains);
  public sealed record AlertRuleReach(int RuleId, int AppliesTo, int Hidden, IReadOnlyList<string> HiddenByRuleNames);
  public static class AlertRulePresentation
  {
      /// "All domains", "Group Aurora Retail and tag Finance", "All domains except shop.aurora-retail.example", a deleted target as "a group that no longer exists".
      public static string DescribeScope(AlertRule rule, AlertScopeNames names, bool hasDomainScope);   // a system kind is "Not about a domain"
      /// For the rules of one kind in processing order, how many of the given domains each applies to, and how many it would match
      /// but an earlier rule that stops processing takes first, naming those rules. A disabled rule applies to none.
      public static IReadOnlyList<AlertRuleReach> Reach(IReadOnlyList<AlertRule> rulesOfKind, IReadOnlyList<DomainFacts> domains);
      public static bool IsDeadTarget(AlertRuleScopeEntry entry, AlertScopeNames names);
  }
  ```
- [ ] **Step 1: Write the failing tests:** `AllDomains_WhenThereIsNoInclude`, `IncludesAreListedByKind_UsingTheirNames`, `ExcludesAreAppended`, `ASystemKind_SaysItIsNotAboutADomain`, `ADeletedTarget_IsNamedAsGone`, `Reach_CountsDomainsAndHonoursStop` (rule A stops and covers group 1, rule B all: A applies to the group's domains, B to the rest, B hides the group's domains behind A and names it), `Reach_ADisabledRuleAppliesToNone_AndHidesNothing`, `Reach_ARuleThatDoesNotStopHidesNothing`, `Reach_ExcludesAreRespected`, `IsDeadTarget_IsTrueOnlyForAMissingId`.
- [ ] **Step 2: Run to verify they fail**. Expected: compile errors.
- [ ] **Step 3: Implement** (pure functions; reuse the same match logic as `AlertRuleSet` by extracting it to an internal static `AlertRuleScopeMatcher.Matches(AlertRule, DomainFacts)` that both use, and keep `AlertRuleSetTests` passing).
- [ ] **Step 4: Run** `--filter "AlertRulePresentationTests|AlertRuleSetTests"`. Expected: all pass.
- [ ] **Step 5: Commit**: `git commit -m "Describe a rule's scope in words and work out which domains it reaches"`.

---

### Task 8: The Alert rules page

**Files:**
- Create: `src/DotMarc/Components/Pages/AlertRules.razor`, `src/DotMarc/Components/Dialogs/AlertRuleDialog.razor`
- Modify: `src/DotMarc/Components/Layout/MainLayout.razor` (a Manage menu link "Alert rules", shown for `AlertsView`)
- Test: browser verification (below), plus `test/DotMarc.Tests/Components/AlertRulesPageTests.cs` for the route and policy

**Interfaces:**
- Consumes: Tasks 1 to 7. The page reads rules with `AlertRules.Include(Scope)` and the names for groups, tags and domains, builds `DomainFacts` for monitored domains, and calls `AlertRulePresentation`.
- Page `@page "/alerts/rules"`, `[Authorize(Policy = "AlertsView")]`. Every add, edit, delete, move, reset, enable and clean-up control is inside `<AuthorizeView Policy="AlertRulesManage">`, and every handler that writes re-checks with the same policy through `IAuthorizationService` (a handler only runs for a rendered control, but the check keeps a stale circuit safe).
- Layout: a heading with `DocsLink`, one section per kind in `AlertTypes.All` order (system kinds last, under "Not about a domain"). Each section has the kind's description, a table of its rules in processing order (move up and down buttons, name, scope in words, severity chip, an on/off switch, a "Stops" chip, "Applies to N domains" and, when hidden, "N hidden behind: name"), and an Add rule button. A dead scope shows a warning chip with a Clean up button for people who may manage.
- Dialog: name; severity select; enabled and Stop processing switches (with a one-line help each); scope (three include lists and three exclude lists as multi-select autocompletes of groups, tags and domains; hidden for a system kind); one field per `AlertRuleParameterDefinition` (integer field with its range and help, or a select), hiding a parameter whose `ShowWhenKey` does not match; cooldown (blank uses the global) and, for the two kinds that have it, "Close after (days)"; a live "Applies to N domains" and a warning naming any earlier stopping rule that hides this one. Save shows the validator's problems inline and does not close. Default rules offer Reset to default and no delete.

- [ ] **Step 1: Write the failing test** `AlertRulesPageTests`: `ThePageRequiresAlertsView` (the endpoint metadata or an anonymous request is refused; follow `StaticPagesTests`), and `TheMenuLinksToTheRulesPage` if the existing tests inspect the nav.
- [ ] **Step 2: Implement** the page and dialog and the menu link, following the patterns in `AlertsSettings.razor`, `ManageGroups.razor` and an existing dialog (for example `GroupTicketRulesDialog.razor`). Keep logic in the helpers and service; the razor files only wire them.
- [ ] **Step 3: Build**: `dotnet build src/DotMarc`. Expected: success, no warnings.
- [ ] **Step 4: Verify in the browser** (recipe: docker `postgres:18` container `dotmarc-verify` on port 55432, `Demo__Enabled=true`, Edge via playwright-edge, sign in as Demo Admin). Check, and look at each screenshot: the page lists every kind with its default rule and the demo's Aurora Retail rule above the default missing-report rule; adding a rule from the dialog works and appears first; an out-of-range number shows its message and keeps the dialog open; the live reach number changes as scope changes and a hiding rule is named; moving a rule changes the order; a default rule shows Reset and no Delete; dark mode is readable (secondary text especially); then sign in as Demo Viewer: the rules show but no controls do. Stop the app and remove the container afterwards.
- [ ] **Step 6: Commit**: `git commit -m "Add the Alert rules page"`.

---

### Task 9: Trim the Alert settings page, and show the rule on alerts

**Files:**
- Modify: `src/DotMarc/Components/Pages/AlertsSettings.razor`, `src/DotMarc/Notifications/NotificationSettingsService.cs`, `src/DotMarc/Components/Pages/Alerts.razor`, `src/DotMarc/Api/AlertEndpoints.cs`, `src/DotMarc/Api/ApiModels.cs`, `src/DotMarc/Api/ApiDocument.cs` (example), `website/data/openapi/dotmarc-api.json` (regenerated)
- Test: `test/DotMarc.Tests/Notifications/NotificationSettingsServiceTests.cs` (update), `test/DotMarc.Tests/Api/AlertEndpointTests.cs` (extend)

**Interfaces:**
- `NotificationSettingsService.SaveAsync` stops validating and auditing the retired fields (missing report days, suspicious reject numbers, the seven DNS modes, policy and nameserver switches, auto-close days, the new sender and delivered fields), and no longer copies them. The page stops showing them and gains a short note with a link: "Thresholds, severity and scope now live in Alert rules."
- `ApiAlert` gains `Rule` (`ApiNamedRef?`: id and name of the rule that raised it, null when none); the alerts list in the app gains a Rule column (the rule's name, empty when none).

- [ ] **Step 1: Write the failing tests:** `SavingSettings_NoLongerChangesTheRetiredFields` (save with different retired values, the stored row's retired fields are unchanged), `SavingSettings_StillValidatesWebhookUrls_AndAuditsTheRemainingFields`, `TheAlertsApi_ReturnsTheRaisingRule` (an alert with a rule returns `{ id, name }`; one without returns null), `TheAlertsApi_RuleFieldIsInTheOpenApiDocument` (the existing committed-copy test fails until the document is regenerated).
- [ ] **Step 2: Run to verify they fail**, implement, regenerate the OpenAPI copy (`node scripts/update-openapi.mjs`).
- [ ] **Step 3: Run** `--filter "FullyQualifiedName~Notifications|FullyQualifiedName~Api|FullyQualifiedName~Audit"`. Expected: all pass.
- [ ] **Step 4: Verify in the browser** (same recipe): the Alert settings page shows the switch, channels, cooldown, monitor interval, ticket rules and the note; saving still works; the Alerts list shows a Rule column.
- [ ] **Step 5: Commit**: `git commit -m "Move the alert thresholds to the rules and show which rule raised an alert"`.

---

### Task 10: Demo and docs

**Files:**
- Modify: `src/DotMarc/Demo/DemoDataSeeder.cs`, `website/docs/alerts.mdx`, `website/docs/getting-started.mdx`
- Test: `test/DotMarc.Tests/Demo/DemoAlertRulesTests.cs` (extend)

- [ ] **Step 1: Write the failing test** `TheDemoHasAnUnderReviewExample`: a second custom rule, `NewUnreviewedSender`, scoped to the group "Cobalt Freight" (use whichever group the demo seeds for cobalt-freight.example; assert by name), with `underReview` `afterDays` and `underReviewDays` `14`, and `StopProcessing` true, above the default; and `TheDemoRulesAreAllExplicit` (every rule that has parameters stores them).
- [ ] **Step 2: Implement** in the seeder after the existing demo rule; run `--filter "FullyQualifiedName~Demo"`.
- [ ] **Step 3: Write the docs.** `alerts.mdx`: replace the settings table rows that moved with a new "Alert rules" section (what a rule is, ordering and Stop processing with the "a new rule goes above the default and replaces it" explanation, scope include and exclude, severity including Critical, the per-kind parameters, Under review handling with the three choices, cooldown and auto-close, reset to default, the permission, and the audit log), keeping the "Alert settings" section for the switch, channels, cooldown and monitor interval. `getting-started.mdx`: one sentence in the decision section that Under review can alert again after a number of days, linking to the rules section. No em dashes. The website cannot be built on this machine (website installs need WSL), so re-read the MDX for unclosed tags and broken links by hand and say so in the report.
- [ ] **Step 4: Run the whole suite**: `dotnet test dotMARC.sln`. Expected: 0 failed, no build warnings.
- [ ] **Step 5: Commit**: `git add -A && git commit -m "Show alert rules in the demo and document them"`.
