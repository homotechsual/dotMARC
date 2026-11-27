# DMARC target policy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A target DMARC policy set system-wide, per group and per domain, resolved once per domain (domain, then strictest group, then system), used by a new below-target alert, by the `dmarc` push, by the domain list and API, and managed on a tabbed Baselines page.

**Architecture:** Nullable target columns on `DnsRecordSettings`, `Group` and `Domain`. A pure resolver in a new `DotMarc.Baselines` namespace turns them into an `EffectiveDmarcTarget` with its source, and a pure check turns that plus the stored DMARC observation into an outcome. Every consumer (alert evaluator, change builder, pages, API) calls those two, never its own rules. A shared tab shell component hosts the Baselines tabs and is reused by #76 and #77.

**Tech Stack:** .NET 10, Blazor (MudBlazor), EF Core + Npgsql, xUnit with the Postgres container fixture, Docusaurus docs.

**Spec:** [DMARC target policy](../specs/2026-11-24-dmarc-target-policy-design.md) (item 20 of [the 0.14.0 roadmap](2026-11-20-v0-14-0-roadmap.md), issue #75). The tab shell follows [tabbed settings pages](../specs/2026-11-24-tabbed-settings-pages-design.md).

## Global Constraints

* No em dashes in any prose, comment, UI copy or commit message. Meaningful variable names, including in tests. Warnings fail the build. No hard line wraps in markdown.
* **Upgrade changes nothing:** with no target set at any level there are no new alerts, push plans are byte-for-byte what they were, and no domain is below target. Every existing test in `test/DotMarc.Tests/DnsPush` and `test/DotMarc.Tests/Notifications` passes unchanged.
* Resolution order: domain override, else the **strictest** of the domain's groups' targets (per tag, `p` and `sp` separately), else the system target, else none. A group target overrides the system target even when weaker.
* `sp` unset means it follows `p`. `pct` is not configurable; meeting a target needs effective `pct` 100.
* The push never lowers `p`, `sp` or `pct`.
* Permission `BaselinesManage`, appended last to the `Permission` enum. Unlimited staff: every level. Group-limited staff: their own groups and domains in them, never the system target. Portal users: nothing.
* The strictest-wins rule is visible on the Baselines Domain tab (not a tooltip) and in the docs as a warning callout with a worked example.
* Branch `develop`, one commit per task, no push without the user's go-ahead. The migration is added with `dotnet ef migrations add AddDmarcTargets --project src/DotMarc`, and `PsaDataMigrationTests` is re-run.
* Browser checks use the playwright-edge tools, never an installed Chrome.

## Review Focus

* A domain in two groups where one sets only `p=reject` and the other sets `p=none; sp=reject` resolves per tag to `p=reject; sp=reject` and names both groups (Task 1).
* A stale observation: a domain whose last check failed keeps its previous stored policy. `NotChecked` is "needs checking"; a domain with a stored policy from an earlier successful check is judged on it (Task 1 test pins this).
* A record with `pct=50` and otherwise at target is below target, and the push removes the `pct` tag, keeping every other tag in order (Tasks 1 and 4).
* A group-limited editor cannot set a target on a domain that is in none of their groups, or on the system, even by calling the service directly with a crafted id (Task 2).
* Deleting a group with a target leaves its former domains resolving to the system target (or their own), with no orphaned state (Task 2).

---

### Task 1: Target model, resolver and outcome

**Files:**
- Create: `src/DotMarc/Baselines/DmarcTarget.cs`, `src/DotMarc/Baselines/DmarcTargetResolver.cs`, `src/DotMarc/Baselines/DmarcTargetCheck.cs`, `src/DotMarc/Baselines/DmarcTargetText.cs`
- Test: `test/DotMarc.Tests/Baselines/DmarcTargetResolverTests.cs`, `test/DotMarc.Tests/Baselines/DmarcTargetCheckTests.cs`

**Interfaces:**
- Produces:
  * `record DmarcTarget(DmarcPolicyLevel Policy, DmarcPolicyLevel? SubdomainPolicy)` with `DmarcPolicyLevel EffectiveSubdomainPolicy => SubdomainPolicy ?? Policy`. Stored shape at every level.
  * `enum DmarcTargetSource { Domain, Group, System }`.
  * `record EffectiveDmarcTarget(DmarcPolicyLevel Policy, DmarcPolicyLevel SubdomainPolicy, DmarcTargetSource Source, IReadOnlyList<string> ContributingGroups, string? PolicyGroup, string? SubdomainGroup)`. The group fields are set only when `Source` is `Group`; `ContributingGroups` lists every group of the domain that has a target, ordered by name; `PolicyGroup`/`SubdomainGroup` name the winner per tag (ties go to the first by name, ordinal ignore case).
  * `static EffectiveDmarcTarget? DmarcTargetResolver.Resolve(DmarcTarget? domainTarget, IEnumerable<(string GroupName, DmarcTarget Target)> groupTargets, DmarcTarget? systemTarget)`.
  * `static EffectiveDmarcTarget? DmarcTargetResolver.Resolve(Domain domain, DnsRecordSettings settings)`: reads the columns added in Task 2 (requires `domain.Groups` loaded). Write it in Task 2 once the columns exist; Task 1 ships the pure overload only.
  * `enum DmarcTargetOutcome { NoTarget, Meets, Below, NeedsChecking }`.
  * `static DmarcTargetOutcome DmarcTargetCheck.Outcome(Domain domain, EffectiveDmarcTarget? target)`.
  * `static string DmarcTargetText.Source(EffectiveDmarcTarget target)` and `static string DmarcTargetText.Policy(EffectiveDmarcTarget target)`.

- [ ] **Step 1: Write failing resolver tests**, one fact each:
  * nothing set at any level gives null;
  * system only gives Source `System`;
  * a group target beats the system target, including a weaker group (`system p=reject`, group `p=none` gives `None`);
  * a domain override beats groups and system;
  * two groups `Contoso p=reject` and `Parked-Estate p=quarantine` give `Reject`, `PolicyGroup "Contoso"`, `ContributingGroups ["Contoso", "Parked-Estate"]`;
  * per tag: `A p=reject` and `B p=none sp=reject` give `p=Reject (A), sp=Reject`, and `sp=quarantine` alone on a group with `p=none` resolves `sp` from that group;
  * an unset `sp` follows that level's `p` (`p=quarantine` gives `SubdomainPolicy Quarantine`);
  * a domain override with `p` only gives `sp` equal to that `p`, not a group's stricter `sp` (the override replaces the whole target).
- [ ] **Step 2: Write failing outcome tests** (`Domain` built in memory with `DmarcCheckStatus`, `DmarcCheckedUtc`, `DmarcPolicy`, `DmarcSubdomainPolicy`, `DmarcPercent`):
  * null target gives `NoTarget`;
  * `NotChecked` or null `DmarcCheckedUtc` gives `NeedsChecking`;
  * `MissingOwnRecord` (policy null) gives `Below`; `Misconfigured` with policy null gives `Below`;
  * stored `reject/reject/100` against target `quarantine` gives `Meets` (stricter is fine);
  * `quarantine/none/100` against `p=quarantine sp=quarantine` gives `Below`;
  * `reject/reject/50` against `reject` gives `Below`;
  * a domain whose `DmarcCheckStatus` is `Ok` from an earlier check, with stored `none`, against `reject`, gives `Below` (judged on the last stored observation).
- [ ] **Step 3: Write failing text tests:** `Source` gives "set on this domain", "system-wide", "from group Contoso", and with two contributing groups "strictest of Contoso and Parked-Estate" (three or more: "strictest of A, B and C"). `Policy` gives `p=reject` when `sp` equals `p`, else `p=reject; sp=quarantine`.
- [ ] **Step 4: Run** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DotMarc.Tests.Baselines"`. Expected: FAIL (types missing).
- [ ] **Step 5: Implement** the four files. `DmarcTargetCheck.Outcome` uses `DmarcPolicyTags.Of(domain)` for the observation and compares each of `p`, `sp` with `>=` and `pct == 100`.
- [ ] **Step 6: Run** the same filter. Expected: PASS.
- [ ] **Step 7: Commit** `Resolve a domain's DMARC target and judge it`.

### Task 2: Storage, permission, access and saving

**Files:**
- Modify: `src/DotMarc/Data/Domain.cs`, `src/DotMarc/Data/Group.cs`, `src/DotMarc/Notifications/DnsRecordSettings.cs`, `src/DotMarc/Data/DotMarcDbContext.cs` (string conversions as for `DmarcPolicy`), `src/DotMarc/Data/Permission.cs`, `src/DotMarc/Audit/AuditActions.cs` (or wherever `DnsRecordSettingsSaved` is defined), `src/DotMarc/Baselines/DmarcTargetResolver.cs` (entity overload)
- Create: `src/DotMarc/Baselines/BaselineAccess.cs`, `src/DotMarc/Baselines/DmarcTargetService.cs`, migration `AddDmarcTargets`
- Test: `test/DotMarc.Tests/Baselines/BaselineAccessTests.cs`, `test/DotMarc.Tests/Baselines/DmarcTargetServiceTests.cs`, `test/DotMarc.Tests/Baselines/BaselinesPermissionTests.cs`

**Interfaces:**
- Consumes: `DmarcTarget`, `DmarcTargetResolver.Resolve` (Task 1).
- Produces:
  * Columns `DmarcPolicyLevel? TargetDmarcPolicy` and `DmarcPolicyLevel? TargetDmarcSubdomainPolicy` on `Domain`, `Group` and `DnsRecordSettings`, plus `DmarcTarget? DmarcTarget` read helpers (not mapped) on each: null when `TargetDmarcPolicy` is null.
  * `Permission.BaselinesManage` (last value).
  * `static bool BaselineAccess.MayChangeSystem(ClaimsPrincipal user)`, `MayChangeGroup(ClaimsPrincipal user, int groupId)`, `MayChangeDomain(ClaimsPrincipal user, IReadOnlyCollection<int> domainGroupIds)`. Same scope rules as `SenderDecisionAccess`; a limited person may change a domain only when one of its groups is theirs. The `BaselinesManage` policy is checked separately, as for senders.
  * `DmarcTargetService.SetSystemAsync(DotMarcDbContext context, AuditActor actor, DmarcTarget? target, CancellationToken cancellationToken) : Task`
  * `DmarcTargetService.SetGroupAsync(DotMarcDbContext context, AuditActor actor, int groupId, DmarcTarget? target, CancellationToken cancellationToken) : Task<bool>` (false when the group does not exist)
  * `DmarcTargetService.SetDomainAsync(DotMarcDbContext context, AuditActor actor, int domainId, DmarcTarget? target, CancellationToken cancellationToken) : Task<bool>`
  * Audit action `DmarcTargetSaved = "baseline.dmarc_target_saved"`, display text "DMARC target saved", one `AuditChanges` field "DMARC target" with old and new as `DmarcTargetText`-style values or "not set"; target `AuditTarget.Settings("Baselines")`, `AuditTarget.For(group)` or `AuditTarget.For(domain)`. No entry when nothing changed.
  * `DmarcTargetResolver.Resolve(Domain domain, DnsRecordSettings settings)`.

- [ ] **Step 1: Write failing access tests** mirroring `SenderDecisionAccessTests`: unlimited may change system, any group, any domain (including one in no group); limited to groups 3 and 4 may not change system, may change group 3, not group 5, may change a domain in groups [4, 9], not one in [9] or in no group; a portal user may change nothing.
- [ ] **Step 2: Write failing service tests** (Postgres fixture): setting each level stores it and records one audit entry with old "not set" and the new value; saving the same value records nothing; clearing records old value and "not set"; an unknown group or domain id returns false and records nothing; a target with `SubdomainPolicy` equal to `Policy` is stored with `SubdomainPolicy` null (follows p); deleting a group that had a target leaves its domains resolving to the system target through `Resolve(domain, settings)`.
- [ ] **Step 3: Write failing permission tests** copying `AlertRulesPermissionTests`: Admin has `BaselinesManage` after bootstrapping; a custom role does not get it.
- [ ] **Step 4: Run** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DotMarc.Tests.Baselines"`. Expected: FAIL.
- [ ] **Step 5: Implement** columns, conversions, permission, access, service, audit action and the entity resolver overload. Add the migration, nullable columns only.
- [ ] **Step 6: Run** the Baselines filter and `--filter "FullyQualifiedName~PsaDataMigrationTests|FullyQualifiedName~AccessBootstrapper"`. Expected: PASS.
- [ ] **Step 7: Commit** `Store DMARC targets at system, group and domain level`.

### Task 3: Below-target alert

**Files:**
- Modify: `src/DotMarc/Notifications/AlertTypes.cs`, `src/DotMarc/Notifications/DnsHealthItems.cs`, `src/DotMarc/Notifications/DnsHealthAlertEvaluator.cs`, `src/DotMarc/Notifications/AlertingService.cs` (`CheckDnsHealthAsync`), `src/DotMarc/Ingestion/PollingService.cs` (DMARC recheck query)
- Test: `test/DotMarc.Tests/Notifications/DnsHealthAlertEvaluatorTests.cs`, `test/DotMarc.Tests/Notifications/DnsHealthRuleAlertingTests.cs`, `test/DotMarc.Tests/Notifications/AlertTypesTests.cs`

**Interfaces:**
- Consumes: `EffectiveDmarcTarget`, `DmarcTargetCheck.Outcome`, `DmarcTargetText`, `DmarcTargetResolver.Resolve(Domain, DnsRecordSettings)`.
- Produces:
  * `AlertTypes.DmarcPolicyBelowTarget = "DmarcPolicyBelowTarget"`, `AlertTypeInfo(..., "DMARC policy below target", "The domain's DMARC policy is weaker than the target set for it.", CreatesTicketByDefault: false)`, listed after `DmarcPolicyWeakened` in `All` and in `DnsHealth`.
  * `DnsHealthItems.DmarcTarget = "DmarcTarget"`.
  * `DnsHealthAlertEvaluator.Evaluate(Domain domain, List<DomainAlertState> states, AlertRuleSet rules, DateTimeOffset nowUtc, EffectiveDmarcTarget? dmarcTarget = null)`.

The new kind gets its default rule from `AlertRuleSeeder.EnsureDefaultPackAsync` with no further work (enabled, since `AlertRuleLegacySettings.IsEnabled` falls through to true). It is not acknowledgeable: leave `HasAutoClose` and `AlertRuleLegacySettings.AutoCloseDays` alone.

- [ ] **Step 1: Write failing evaluator tests** for `EvaluateTarget`, using the confirmation pattern of the existing policy tests:
  * no target: resolves `DmarcPolicyBelowTarget`, raises nothing;
  * meets: resolves;
  * below: first cycle starts pending and raises nothing; after a check at or past `RecheckDueUtc`, raises for each applicable rule with the message "The DMARC policy for example.com is p=none; its target is p=reject (from group Contoso)." Both sides are written in the `DmarcTargetText.Policy` form, and the observed side gains `; pct=50` when its percentage is below 100;
  * missing record (`MissingOwnRecord`): clears pending and neither raises nor resolves (the DMARC record broken alert covers it);
  * needs checking: clears pending, neither raises nor resolves;
  * no applicable rule: resolves and clears pending.
- [ ] **Step 2: Write a failing alerting test** in `DnsHealthRuleAlertingTests` style: with a system target `reject` and a domain whose stored policy is `none`, two cycles past confirmation open one `DmarcPolicyBelowTarget` alert; clearing the system target and running a cycle resolves it. A second test: with no targets anywhere, the cycle creates no alert of the new type.
- [ ] **Step 3: Run** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DnsHealth|FullyQualifiedName~AlertTypes|FullyQualifiedName~AlertRuleKinds|FullyQualifiedName~AlertRuleSeeder"`. Expected: the new tests FAIL; adjust any existing count assertion in `AlertTypesTests`/`AlertRuleKindsTests` only where it lists every type.
- [ ] **Step 4: Implement.** `CheckDnsHealthAsync` loads `DnsRecordSettings` once per cycle and passes `DmarcTargetResolver.Resolve(domain, settings)` per domain (domains already include `Groups`). `PollingService`'s stale-domain query treats `DnsHealthItems.DmarcTarget` like `DmarcPolicy`.
- [ ] **Step 5: Run** the same filter and the whole `Notifications` folder. Expected: PASS.
- [ ] **Step 6: Commit** `Alert when a domain's DMARC policy is below its target`.

### Task 4: Push raises the record to the target

**Files:**
- Create: `src/DotMarc/Baselines/DmarcPolicyRaise.cs`, `src/DotMarc/Baselines/DmarcTargetReader.cs`
- Modify: `src/DotMarc/DnsPush/DmarcChangeBuilder.cs`, `src/DotMarc/Program.cs` (register the reader)
- Test: `test/DotMarc.Tests/Baselines/DmarcPolicyRaiseTests.cs`, `test/DotMarc.Tests/DnsPush/DnsChangeBuilderTests.cs`

**Interfaces:**
- Consumes: `EffectiveDmarcTarget`, `DmarcTargetResolver.Resolve(Domain, DnsRecordSettings)`.
- Produces:
  * `static string DmarcPolicyRaise.Apply(string record, EffectiveDmarcTarget target)`: raises `p` and `sp` to at least the target, removes a `pct` below 100, keeps every other tag and the tag order. Adds `sp=` after `p=` only when the record has no `sp` and the target `sp` is stricter than the record's new `p`. Returns the input unchanged when it already meets the target.
  * `static string DmarcPolicyRaise.NewRecordPolicy(EffectiveDmarcTarget? target)`: `"p=none"` for null, else `DmarcTargetText.Policy(target)`.
  * `interface IDmarcTargetReader { Task<EffectiveDmarcTarget?> GetAsync(int domainId, CancellationToken cancellationToken); }` and `DmarcTargetReader(IDbContextFactory<DotMarcDbContext>)`, loading the domain with `Groups` and the settings.
  * `DmarcChangeBuilder(IDmarcTxtLookup, IOptions<GraphOptions>, IDnsRecordSettingsReader, IDmarcTargetReader)`.

- [ ] **Step 1: Write failing raise tests:** `v=DMARC1; p=none; rua=mailto:a@x` with target reject gives `v=DMARC1; p=reject; rua=mailto:a@x`; `v=DMARC1; p=quarantine; sp=none; pct=50; fo=1` with target `p=quarantine sp=quarantine` gives `v=DMARC1; p=quarantine; sp=quarantine; fo=1`; `v=DMARC1; p=reject` with target quarantine is unchanged; `v=DMARC1; p=none` with target `p=quarantine sp=reject` gives `v=DMARC1; p=quarantine; sp=reject`; `v=DMARC1; p=none` with target `p=reject sp=none` gives `v=DMARC1; p=reject` (implicit sp is reject, never lowered); tag names in upper case (`P=none`) are recognised and the original casing of other tags is kept.
- [ ] **Step 2: Write failing builder tests** with a `FakeDmarcTargetReader`: no target gives exactly today's plans (existing tests pass with a reader returning null); a missing record with target reject creates `v=DMARC1; p=reject; rua=...`; an existing below-target record merges `rua` and raises in one `Merge` change; an existing stricter record only merges `rua`; a CNAME-delegated record's replacement uses `p=reject`.
- [ ] **Step 3: Run** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DmarcPolicyRaise|FullyQualifiedName~DnsChangeBuilder|FullyQualifiedName~DotMarc.Tests.DnsPush"`. Expected: new tests FAIL.
- [ ] **Step 4: Implement.** In the builder, raise after `DmarcRuaMerge.TryMerge`; an unmergeable record is still refused as today. Give existing test construction a null-returning fake reader.
- [ ] **Step 5: Run** the same filter. Expected: PASS, including every existing DnsPush test.
- [ ] **Step 6: Commit** `Push DMARC records up to their target`.

### Task 5: Tab shell, Baselines menu and the Sender tab

**Files:**
- Create: `src/DotMarc/Components/Shared/TabbedPage.razor`, `src/DotMarc/Components/Pages/LegacyRedirects.razor`, `src/DotMarc/Components/Pages/BaselinesIndex.razor`
- Modify: `src/DotMarc/Components/Pages/SenderDefaults.razor`, `src/DotMarc/Components/Layout/MainLayout.razor`, `website/docs/getting-started.mdx` (menu path)
- Test: `test/DotMarc.Tests/Components/BaselinesPagesTests.cs`

**Interfaces:**
- Produces:
  * `record TabbedPageTab(string Title, string Href, bool Visible)`.
  * `TabbedPage` parameters: `string Title`, `IReadOnlyList<TabbedPageTab> Tabs`, `RenderFragment ChildContent`. Renders the title and only visible tabs, marks the tab whose `Href` matches the current path as active, and navigates on tab change. Each tab is its own routable page that wraps its content in `TabbedPage`; there is no single page holding every tab's markup.
  * `static class BaselineTabs { static IReadOnlyList<TabbedPageTab> For(ClaimsPrincipal user, bool canManageBaselines, bool canReviewSenders) }`, the one list both Baselines pages render. Sender is visible when the person has `SenderReview` and `SenderDecisionAccess.MayChangeBaseline`; Domain when they have `BaselinesManage`.
  * `LegacyRedirects` carries `@page "/senders/baseline"` and redirects to `/baselines/sender` with `replace: true`, keeping the query string, from one route map that #76 and #77 extend.
  * `BaselinesIndex` at `/baselines` redirects to the first visible tab, or shows "You do not have access to any baselines." when none.

- [ ] **Step 1: Write failing tests** in the reflection style of `AlertRulesPageTests`: `SenderDefaults` routes include `/baselines/sender` and `/groups/{GroupId:int}/senders` and not `/senders/baseline`; `LegacyRedirects` routes include `/senders/baseline`; `BaselinesIndex` is at `/baselines`; `BaselineTabs.For` gives Sender and Domain for an unlimited person with both permissions, only Domain for a group-limited person with `BaselinesManage` (they cannot change the sender baseline), and nothing for a portal user. Also extend `TextLinksTests`/`DocsLinkTests` expectations only if they enumerate menu text.
- [ ] **Step 2: Run** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~BaselinesPages"`. Expected: FAIL.
- [ ] **Step 3: Implement.** `SenderDefaults` wraps its system view in `TabbedPage` (the group view at `/groups/{id}/senders` keeps its Back button and no tabs). The menu replaces "Sender baseline" with one "Baselines" entry to `/baselines`, shown when either tab is visible. Update the docs line "Manage > Sender baseline" to "Manage > Baselines > Sender".
- [ ] **Step 4: Run** the filter and the `Components` folder. Expected: PASS.
- [ ] **Step 5: Browser check** (demo or local run, playwright-edge): the menu shows Baselines; `/senders/baseline?x=1` lands on `/baselines/sender?x=1`; the Sender tab behaves as before; refresh and Back keep the tab.
- [ ] **Step 6: Commit** `Group baselines onto one tabbed page`.

### Task 6: Domain baselines tab, group action and domain card

**Files:**
- Create: `src/DotMarc/Components/Pages/BaselinesDomain.razor`, `src/DotMarc/Components/Dialogs/DmarcTargetDialog.razor`, `src/DotMarc/Baselines/DmarcTargetSummary.cs`
- Modify: `src/DotMarc/Components/Pages/ManageGroups.razor`, `src/DotMarc/Components/Pages/DomainDetail.razor`
- Test: `test/DotMarc.Tests/Baselines/DmarcTargetSummaryTests.cs`, `test/DotMarc.Tests/Components/BaselinesPagesTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 2 and 5.
- Produces:
  * `DmarcTargetSummary.LoadAsync(DotMarcDbContext context, ClaimsPrincipal user, CancellationToken cancellationToken) : Task<DmarcTargetSummary>` with `DmarcTarget? System`, `IReadOnlyList<GroupTargetRow> Groups` (`int Id, string Name, DmarcTarget? Target, int DomainCount`), `IReadOnlyList<DomainOverrideRow> Overrides` (`int Id, string Name, DmarcTarget Target`), `int BelowCount`, `int NeedsCheckingCount`. Counts use the resolver and `DmarcTargetCheck` over monitored domains the person can see (`PortalData.ScopedGroupIds` scope), so they match the filtered list in Task 7.
  * `DmarcTargetDialog` parameters `string Heading`, `DmarcTarget? Current`; returns `DmarcTarget?` (null is "not set"). `p` choices: Not set, None, Quarantine, Reject. `sp` choices: Follows p, None, Quarantine, Reject, disabled while `p` is Not set.
  * `BaselinesDomain` at `/baselines/domain`, `[Authorize(Policy = "BaselinesManage")]`.

- [ ] **Step 1: Write failing summary tests** (Postgres): counts below and needs-checking over monitored domains only; a group-limited person sees only their groups, their domains' overrides and counts over their domains; group `DomainCount` counts members.
- [ ] **Step 2: Write failing route test:** `BaselinesDomain` is at `/baselines/domain` and requires `BaselinesManage`; `DmarcTargetDialog` is not routable.
- [ ] **Step 3: Run** the Baselines and BaselinesPages filters. Expected: FAIL.
- [ ] **Step 4: Implement the tab.** Sections in order: system target (editable only when `BaselineAccess.MayChangeSystem`, otherwise read-only text); the strictest-wins notice as a visible `MudAlert` (Severity Info) with this copy: "A domain in more than one group uses the strictest of those groups' targets. A group's target replaces the system-wide target for its domains, even when it is weaker. A domain's own target replaces both."; group targets table with a Change action per row the person may change; domain overrides list linking to each domain; "N domains below target" linking to `/dashboard?target=below` and "N need checking" linking to `/dashboard?target=checking`.
- [ ] **Step 5: Implement the group action and domain card.** ManageGroups gets a "Domain baseline" action beside Sender defaults, shown with `BaselinesManage` and `BaselineAccess.MayChangeGroup`, opening the dialog and calling `SetGroupAsync`. DomainDetail shows "DMARC target: p=reject, from group Contoso (strictest of 2)" style text via `DmarcTargetText` and the outcome (Meets target, Below target, Needs checking, No target) for anyone with `DomainsView`, plus Set/Change/Clear for people with `BaselinesManage` and `BaselineAccess.MayChangeDomain`. With no target the card reads "No DMARC target applies." and offers Set only to permitted people.
- [ ] **Step 6: Run** the filters. Expected: PASS.
- [ ] **Step 7: Browser check:** set a system target, a group target and a domain override; the tab, group row, domain card and counts agree; a demo persona limited to one group sees the system target read-only and only their groups.
- [ ] **Step 8: Commit** `Set DMARC targets from the Baselines page, groups and domains`.

### Task 7: Domain list filter and API

**Files:**
- Modify: `src/DotMarc/Components/Pages/Dashboard.razor` (and its row type), `src/DotMarc/Api/ApiModels.cs`, `src/DotMarc/Api/DomainReadEndpoints.cs`, `src/DotMarc/Api/ApiDocument.cs` (example), the OpenAPI copy kept in the repo, `website/docs/api.mdx`
- Test: `test/DotMarc.Tests/Api/DomainReadEndpointTests.cs` (follow its existing host setup)

**Interfaces:**
- Consumes: Tasks 1 and 2.
- Produces:
  * `record ApiDmarcTarget(string Policy, string SubdomainPolicy, string Source, string Outcome)`; `ApiDomain` gains `ApiDmarcTarget? DmarcTarget` (null when no target), values lower case (`"reject"`, `"group"`, `"below"`).
  * Query parameter `dmarcTarget` on the domain list accepting `meets`, `below`, `checking` (repeatable like the other status filters). Any use of it forces the existing in-memory filtering branch (the one taken for `statuses`), so paging stays correct.
  * Dashboard row gains `DmarcTargetOutcome TargetOutcome`; a "Target" column filter (Any, Below target, Needs checking, Meets target) set from `?target=below|checking|meets` on load.

- [ ] **Step 1: Write failing API tests:** a domain below a system target returns `dmarcTarget.outcome "below"` and `source "system"`; with no targets the field is null; `?dmarcTarget=below` returns only below-target domains and a correct `totalCount` across pages; a scoped key sees only its groups' domains as today.
- [ ] **Step 2: Run** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainReadEndpoint|FullyQualifiedName~ApiDocument"`. Expected: FAIL.
- [ ] **Step 3: Implement** the API field, filter, example and OpenAPI copy, and the Dashboard column and query parameter. Add the field and filter to `api.mdx`.
- [ ] **Step 4: Run** the filter and the `Api` folder. Expected: PASS.
- [ ] **Step 5: Browser check:** the Baselines tab's below-target link opens the Dashboard filtered to the same count.
- [ ] **Step 6: Commit** `Filter domains and API results by DMARC target outcome`.

### Task 8: Demo data and docs

**Files:**
- Modify: `src/DotMarc/Demo/DemoDataset.cs`, `src/DotMarc/Demo/DemoDataSeeder.cs`, `website/docs/dns-provider-push.mdx`, `website/docs/alert-rules.mdx`, `website/docs/permissions-and-access.mdx`, `website/sidebars.ts`, `docs/superpowers/plans/2026-11-20-v0-14-0-roadmap.md` (release notes lines)
- Create: `website/docs/baselines.mdx`
- Test: `test/DotMarc.Tests/Demo` (existing seeder tests), `DocsLinkTests`

- [ ] **Step 1: Demo.** Seed a system target `quarantine`, one demo group with `reject`, and leave at least one domain below target and one meeting it, so the alert, tab and filter all show something. Run the Demo tests. Expected: PASS.
- [ ] **Step 2: Docs.** `baselines.mdx` covers both tabs: Sender (link to the sender decisions section) and Domain (levels, `sp` follows `p`, what meets a target, needs checking, the alert, the push). It carries a `:::warning Strictest group wins` callout with the worked example: a domain in Contoso (`p=reject`) and Parked-Estate (`p=quarantine`) with no override uses `p=reject`; a group target replaces the system target even when weaker. Add it to `sidebars.ts` beside `alert-rules`. `dns-provider-push.mdx`: the DMARC push writes the effective target and never lowers a stricter policy, with no target it behaves as before. `alert-rules.mdx`: the new alert. `permissions-and-access.mdx`: `BaselinesManage`, its scope behaviour, and that Admin gets it on upgrade while custom roles do not. The domain baselines tab links to `https://dotmarc.app/docs/baselines`.
- [ ] **Step 3: Release notes.** Add a "Release notes (draft)" list to the roadmap for the 0.14 post: targets at three levels, strictest group wins, the new alert (no tickets by default), the push raising to target, Baselines replacing Sender baseline (old link redirects), `BaselinesManage` (Admin only until added to custom roles), nothing changes until a target is set.
- [ ] **Step 4: Run** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DocsLink|FullyQualifiedName~Demo"` and the full suite `dotnet test test/DotMarc.Tests`. Expected: PASS.
- [ ] **Step 5: Commit** `Document DMARC targets and show them in the demo`.
