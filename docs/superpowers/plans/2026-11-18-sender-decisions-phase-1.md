# Sender Decisions, Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let someone with the new Review senders permission mark a sender on a domain as Authorised, Unauthorised or Under review, and have the sender table show the decision and a fix that depends on it.

**Architecture:** A `SenderDecisions` table holds decisions at all three levels (baseline, group, domain) so Phase 2 needs no migration. A pure resolver picks the winning decision, a pure presentation class writes the fix text, and a small static service writes domain-level decisions with an audit entry. The sender table from the sender identification work gains a status chip and a decision dialog.

**Tech Stack:** C# / .NET 10, EF Core with Npgsql, Blazor Server + MudBlazor 9, xUnit with the Postgres fixture.

**Spec:** `docs/superpowers/specs/2026-11-18-sender-decisions-design.md`. This plan covers its Phase 1 only. Phases 2 to 4 (baseline and group screens, alerts, report and API) get their own plans.

## Global Constraints

- One migration, adding `SenderDecisions` only, with every column the spec lists plus `DecidedByName`.
- The resolution order is: the domain's IP decision, the domain's service decision, the domain's groups' service decisions, the baseline service decision, then Unreviewed. Conflicting group decisions resolve to the most cautious: Unauthorised, then Under review, then Authorised.
- IP decisions exist only at Domain scope.
- Status wording: `Authorised`, `Unauthorised`, `Under review`, and `Unreviewed` for no decision. Copy uses British spelling like the rest of the app.
- Fix wording follows the spec table exactly (see Task 4). The policy line is: none, "Your policy is none, so this mail is still delivered. Move to quarantine or reject to block it."; quarantine, "Your policy is quarantine, so receivers should send it to spam."; reject, "Your policy is reject, so receivers should be rejecting it."
- A new permission `Permission.SenderReview`, appended at the end of the enum so stored values keep their meaning. Seeing decisions needs only the existing domain view permission.
- Audit action code `sender.decision_changed`, label "Sender decision changed". Codes are stored and never change.
- Notes are at most 500 characters. Keys are at most 200.
- No em dashes in copy or docs. Meaningful variable names, even in tests.

## Review Focus

- An Unauthorised sender whose mail passes DMARC (for example the client set up DKIM for it): the fix must say it authenticates as the domain, not "blocked".
- Two people saving a decision for the same sender at the same moment: no exception, one row, the later write wins.
- A group-scope or baseline row with an IP key, or a Domain row with no domain: the database refuses it.
- A decision for a service the catalogue no longer has, or an IP no longer in the reports: it stays in the table and breaks nothing.
- A domain in two groups with conflicting decisions: the most cautious wins and the row says why.
- An Unrecognised row: it has no service, so decisions apply per IP only.

---

### Task 1: The table and its migration

**Files:**
- Create: `src/DotMarc/Data/SenderDecision.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs` (DbSet near line 32, configuration near line 250), `src/DotMarc/Demo/DemoDataSeeder.cs` (truncate list)
- Create: `src/DotMarc/Migrations/<timestamp>_AddSenderDecisions.cs`, its `.Designer.cs`, and update the model snapshot (generated)
- Test: `test/DotMarc.Tests/Data/SenderDecisionSchemaTests.cs`

**Interfaces:**
- Produces, in namespace `DotMarc.Data`:
  - `public enum SenderDecisionScope { Baseline, Group, Domain }`
  - `public enum SenderDecisionKind { Service, Ip }`
  - `public enum SenderDecisionStatus { Authorised, UnderReview, Unauthorised }`
  - `public sealed class SenderDecision` with `int Id`, `SenderDecisionScope Scope`, `int? GroupId`, `int? DomainId`, `SenderDecisionKind Kind`, `required string Key`, `SenderDecisionStatus Status`, `string? Note`, `required string DecidedBy`, `required string DecidedByName`, `DateTimeOffset DecidedUtc`, and constants `MaxKeyLength = 200`, `MaxNoteLength = 500`
  - `DbSet<SenderDecision> SenderDecisions` on `DotMarcDbContext`

- [ ] **Step 1: Write the failing tests** in `SenderDecisionSchemaTests` (`[Collection("Postgres")]`, create a migrated database the way `DashboardPreferencesTests` does):
  - `TheMigrationApplies_AndSavesADomainDecision`
  - `TwoBaselineDecisionsForTheSameService_AreRefused` (`DbUpdateException`; this proves nulls are treated as equal in the unique index)
  - `TheSameServiceOnTwoDomains_IsAllowed`
  - `AGroupDecisionWithoutAGroup_IsRefused`, `ADomainDecisionWithoutADomain_IsRefused`, `ABaselineDecisionWithAnOwner_IsRefused`
  - `AnIpDecisionAtBaselineOrGroupScope_IsRefused`
  - `DeletingADomain_DeletesItsDecisions`, `DeletingAGroup_DeletesItsDecisions`
- [ ] **Step 2: Run** `dotnet test test/DotMarc.Tests --filter SenderDecisionSchemaTests`. Expected: FAIL, types missing.
- [ ] **Step 3: Implement** the entity and the DbSet. Configure in `OnModelCreating`: unique index on `(Scope, GroupId, DomainId, Kind, Key)` with `AreNullsDistinct(false)`; a check constraint that the owner matches the scope (Baseline has neither id, Group has only `GroupId`, Domain has only `DomainId`) and that `Kind = Ip` only when `Scope = Domain`; max lengths from the constants; foreign keys to `Domain` and `Group` with cascade delete and no navigation properties on those entities. Add `"SenderDecisions"` to the demo seeder's truncate list.
- [ ] **Step 4: Generate the migration** with `dotnet ef migrations add AddSenderDecisions --project src/DotMarc` and read it: it must contain only the new table, its index, foreign keys and check constraints.
- [ ] **Step 5: Run** the schema tests. Expected: PASS.
- [ ] **Step 6: Commit** `git commit -m "Add the sender decisions table"`

---

### Task 2: Resolving which decision wins

**Files:**
- Create: `src/DotMarc/Reporting/Senders/SenderDecisionResolver.cs`
- Test: `test/DotMarc.Tests/Reporting/SenderDecisionResolverTests.cs`

**Interfaces:**
- Consumes: `SenderDecision`, its enums.
- Produces, in namespace `DotMarc.Reporting`:
  - `public enum SenderDecisionLevel { None, Domain, Group, Baseline }`
  - `public sealed record ResolvedSenderDecision(SenderDecisionStatus? Status, SenderDecisionLevel Level, SenderDecision? Decision, string? Conflict)` where `Status` is null for Unreviewed
  - `public static class SenderDecisionResolver` with `ResolvedSenderDecision Resolve(IReadOnlyList<SenderDecision> decisions, int domainId, IReadOnlyCollection<int> groupIds, string? serviceName, string ip, IReadOnlyDictionary<int, string> groupNames)`

- [ ] **Step 1: Write the failing tests** (build `SenderDecision` objects directly, no database):
  - `NoDecisions_IsUnreviewed_AtLevelNone`
  - `ADomainIpDecision_BeatsADomainServiceDecision`
  - `ADomainServiceDecision_BeatsAGroupDecision_WhichBeatsTheBaseline`
  - `ABaselineDecision_AppliesWhenNothingMoreSpecificExists`
  - `AnUnrecognisedSource_WithANullServiceName_OnlyMatchesIpDecisions`
  - `AnotherDomainsDecision_IsIgnored`, `AGroupTheDomainIsNotIn_IsIgnored`
  - `ConflictingGroupDecisions_ResolveToTheMostCautious` (Authorised vs Unauthorised gives Unauthorised; Authorised vs Under review gives Under review) with `Conflict` equal to "Aurora Retail says Authorised, Brightline Legal says Unauthorised" (names in group name order)
  - `AgreeingGroupDecisions_HaveNoConflict`
  - `ADomainDecision_SettlesAGroupConflict_WithNoConflictText`
- [ ] **Step 2: Run** `--filter SenderDecisionResolverTests`. Expected: FAIL.
- [ ] **Step 3: Implement `Resolve`** following the order in Global Constraints. The cautiousness rank is Unauthorised, then UnderReview, then Authorised. When several group decisions disagree, the winning one is returned as `Decision` and the others are described in `Conflict`.
- [ ] **Step 4: Run** the tests. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Resolve which sender decision applies"`

---

### Task 3: The permission and the service that saves decisions

**Files:**
- Modify: `src/DotMarc/Data/Permission.cs` (append `SenderReview`), `src/DotMarc/Audit/AuditActions.cs` (code and label)
- Create: `src/DotMarc/Data/SenderDecisionService.cs`
- Test: `test/DotMarc.Tests/Data/SenderDecisionServiceTests.cs`

**Interfaces:**
- Consumes: `SenderDecision`, `AuditActor`, `AuditLog.Record`, `AuditChanges`, `AuditTarget.For(domain)`.
- Produces: `public static class SenderDecisionService` in namespace `DotMarc.Data`:
  - `Task SetAsync(DotMarcDbContext context, AuditActor actor, int domainId, SenderDecisionKind kind, string key, SenderDecisionStatus status, string? note, CancellationToken cancellationToken = default)`
  - `Task ResetAsync(DotMarcDbContext context, AuditActor actor, int domainId, SenderDecisionKind kind, string key, CancellationToken cancellationToken = default)`
  - `Task<List<SenderDecision>> LoadForDomainAsync(DotMarcDbContext context, int domainId, CancellationToken cancellationToken = default)` returning the domain's own decisions, those of every group it is in, and the baseline, no tracking

- [ ] **Step 1: Write the failing tests** (Postgres fixture; `TestActors.Admin` as in `DomainManagementServiceTests`):
  - `SetAsync_CreatesADomainDecision_WithTheActorsNameAndKey_AndAuditsIt` (audit action `sender.decision_changed`, a change from "Unreviewed" to the new status)
  - `SetAsync_ChangesAnExistingDecision_AndAuditsTheOldAndNewStatus`
  - `SetAsync_WithNothingChanged_WritesNoAuditEntry`
  - `SetAsync_RefusesANoteOverFiveHundredCharacters_AndAKeyOverTwoHundred` (`ArgumentException`)
  - `SetAsync_ForAnUnknownDomain_Throws`
  - `ResetAsync_DeletesTheDecision_AndAudits_ButDoesNothingWhenThereIsNone`
  - `TwoCallersSettingTheSameSenderAtOnce_LeaveOneRow_AndBothSucceed` (`Task.WhenAll` over two contexts)
  - `LoadForDomainAsync_ReturnsTheDomainsGroupsAndBaselineDecisions_AndNothingElse`
  - `TheAdminRole_HasSenderReview_AfterBootstrapping` (follow the existing access bootstrapper tests)
- [ ] **Step 2: Run** `--filter SenderDecisionServiceTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** Append `SenderReview` to `Permission`, and the audit action with its label to `AuditActions.All`. `SetAsync` validates lengths, loads the domain, upserts the Domain-scope row, records audit changes with `AuditChanges.Field("Status", ...)` and `Field("Note", ...)`, and returns early when nothing changed. On a unique violation (Postgres 23505) from a concurrent insert, reload the row and update it instead. `ResetAsync` deletes the row and audits. The summary text reads like "Marked Mailchimp as Unauthorised for example.com" and "Reset the decision for Mailchimp on example.com".
- [ ] **Step 4: Run** the tests. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Add the Review senders permission and the service that saves sender decisions"`

---

### Task 4: What a decision does to the fix text

**Files:**
- Create: `src/DotMarc/Reporting/Senders/SenderDecisionPresentation.cs`
- Test: `test/DotMarc.Tests/Reporting/SenderDecisionPresentationTests.cs`

**Interfaces:**
- Consumes: `SenderServiceRow` (its `Name`, `Failing`, `ActedOn`, `Passing`, `Fix`), `ResolvedSenderDecision`, `DmarcPolicyLevel`.
- Produces: `public static class SenderDecisionPresentation` in namespace `DotMarc.Reporting`:
  - `string StatusLabel(SenderDecisionStatus? status)`: "Authorised", "Under review", "Unauthorised", or "Unreviewed" for null
  - `string Provenance(ResolvedSenderDecision decision, IReadOnlyDictionary<int, string> groupNames)`: "set on this domain", "from the {group} group", "from the baseline", or an empty string for none
  - `string? FixFor(SenderServiceRow row, ResolvedSenderDecision decision, DmarcPolicyLevel policy)`

- [ ] **Step 1: Write the failing tests**, one per row of this table, each checking the exact sentence (a decision made by "Sam Lee" on 18 November 2026, Mailchimp, with `row.Fix` set to a sentinel the test can find):

| Decision | Row facts | Result |
|---|---|---|
| Unreviewed or Under review | any | `row.Fix` unchanged |
| Authorised | `Failing` 0 | null |
| Authorised | `Failing` 40 | "You authorised Mailchimp, but 40 messages fail DMARC. " + `row.Fix` |
| Unauthorised | `Failing` 40, `ActedOn` 40 | "Marked unauthorised by Sam Lee on 18 November 2026. Your DMARC policy is blocking it." |
| Unauthorised | `Failing` 40, `ActedOn` 15, policy none | "Marked unauthorised by Sam Lee on 18 November 2026. 25 messages were still delivered. Your policy is none, so this mail is still delivered. Move to quarantine or reject to block it." (the same with the quarantine and reject policy lines for the other two policies) |
| Unauthorised | `Failing` 0, `Passing` 90 | "Marked unauthorised by Sam Lee on 18 November 2026, but 90 messages from it pass DMARC. It authenticates as your domain, so check whether it should be authorised or remove its access." |

  Plus `StatusLabel_ForEachStatusAndNull` and `Provenance_ForEachLevel` (including the group name).
- [ ] **Step 2: Run** `--filter SenderDecisionPresentationTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** Dates are formatted `d MMMM yyyy` in the invariant culture. A decision with no stored row (a null `Decision`) falls back to `row.Fix`.
- [ ] **Step 4: Run** the tests. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Say what an authorised or unauthorised sender decision means"`

---

### Task 5: Decisions in the sender table

**Files:**
- Create: `src/DotMarc/Components/Dialogs/SenderDecisionDialog.razor`
- Modify: `src/DotMarc/Components/Shared/SenderServicesTable.razor`, `src/DotMarc/Components/Pages/DomainDetail.razor`

**Interfaces:**
- Consumes: `SenderDecisionResolver.Resolve`, `SenderDecisionPresentation`, `SenderDecisionService`, `AuditActorAccessor`, `IAuthorizationService` (see `ImportDomains.razor` for the imperative policy check), `Domain.DmarcPolicy`, `Domain.Groups`.
- Produces: `SenderServicesTable` gains parameters `Func<SenderServiceRow, ResolvedSenderDecision?> DecisionFor`, `Func<string, ResolvedSenderDecision> IpDecisionFor`, `DmarcPolicyLevel Policy`, `IReadOnlyDictionary<int, string> GroupNames`, `bool CanReview`, and `EventCallback<(SenderDecisionKind Kind, string Key, string Label)> OnDecide`. `SenderDecisionDialog` returns the chosen status and note, or a reset.

- [ ] **Step 1: Build `SenderDecisionDialog`.** Fields: a status choice (Authorised, Unauthorised, Under review), a note text field limited to 500 characters, a Save button, a Cancel button, and a "Reset to inherited" button that is enabled only when the sender has a decision set on this domain. The title names the sender ("Decide on Mailchimp", or "Decide on 203.0.113.9"). Use a lowercase `title` on icon buttons and enums or constants for anything that would need nested quotes in an attribute.
- [ ] **Step 2: Change `SenderServicesTable`.** Add a Decision column after Evidence: a chip showing `StatusLabel` (colours: Authorised success, Unauthorised error, Under review warning, Unreviewed default), the `Provenance` text beneath it in a caption, and the conflict text in a tooltip when there is one. When `CanReview`, the chip is a button that raises `OnDecide`. Replace the Fix cell with `FixFor`. The expanded part lists each source IP with its own chip and button (the IP's decision overrides the row's). The Unrecognised row shows no row-level decision, only its per-IP list.
- [ ] **Step 3: Wire `DomainDetail.razor`.** Load decisions with `SenderDecisionService.LoadForDomainAsync` in `RebuildSenders`' caller (once at load), resolve per row with the domain's group ids and names, compute `CanReview` once with `AuthorizationService.AuthorizeAsync(user, "SenderReview")`, and handle `OnDecide` by showing the dialog, calling `SetAsync` or `ResetAsync` with `AuditActorAccessor.GetAsync()`, reloading the decisions, and showing a snackbar ("Marked Mailchimp as Unauthorised"). A failure shows the exception message in a snackbar and leaves the page as it was.
- [ ] **Step 4: Build** `dotnet build src/DotMarc`. Expected: no errors or new warnings.
- [ ] **Step 5: Verify in the browser.** Start a throwaway Postgres container and the app in demo mode (as done for the sender table), sign in as Demo Admin, open a domain's Sources tab, and check: a chip on each service row; deciding Unauthorised shows the "Marked unauthorised by" text with the right policy line; the demo domain with a `TrustedForwarder` row behaves sensibly; the per-IP override in an expanded row; Reset returns it to Unreviewed; the Demo Viewer sees chips but no buttons. Look at the screenshots, and stop the app and container afterwards.
- [ ] **Step 6: Commit** `git commit -m "Decide on senders from the Sources tab"`

---

### Task 6: Docs and a full run

**Files:**
- Modify: `website/docs/getting-started.mdx` (the Senders section), `website/docs/permissions-and-access.mdx` (the permission list)

- [ ] **Step 1: Document** the Decision column: the three states and Unreviewed, that a decision is a label and never changes DNS or blocks anything, what each fix says, that an IP decision overrides its service, and the **SenderReview** permission (which the Admin role has, and custom roles can be given). Say plainly that baseline and group decisions come in a later release. No em dashes.
- [ ] **Step 2: Run the full suite** `dotnet test test/DotMarc.Tests`. Expected: all PASS.
- [ ] **Step 3: Commit** `git commit -m "Document sender decisions"`
