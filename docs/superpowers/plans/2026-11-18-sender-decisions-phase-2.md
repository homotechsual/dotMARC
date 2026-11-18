# Sender Decisions, Phase 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let people set a sender decision once for every domain (the baseline) and override it per group, so a sender's answer is made in one place and inherited by the domains that use it.

**Architecture:** Phase 1 already stores decisions at all three levels and the resolver already applies them in order, so this phase adds the writers, the access rules and one screen. `SenderDecisionService` gains baseline and group writes, a small pure class decides who may change which level, and a single page serves both the baseline (`/senders/baseline`) and a group's defaults (`/groups/{id}/senders`).

**Tech Stack:** C# / .NET 10, EF Core with Npgsql, Blazor Server + MudBlazor 9, xUnit with the Postgres fixture.

**Spec:** `docs/superpowers/specs/2026-11-18-sender-decisions-design.md`. This plan is its Phase 2. It builds on `docs/superpowers/plans/2026-11-18-sender-decisions-phase-1.md`, which is done.

## Global Constraints

- No migration. The `SenderDecisions` table, its check constraints and its unique index already cover baseline and group scope.
- Resolution order is unchanged: the domain's IP decision, the domain's service decision, the domain's groups' decisions, the baseline, then Unreviewed. Conflicting group decisions resolve to the most cautious.
- A baseline or group decision is on a **service** only. Never an IP.
- Setting a new baseline or group decision requires a name from `SenderServiceCatalog.All` (by `Name`). A decision already stored for a name that has left the catalogue is kept and shown as "{name} (no longer recognised)".
- Who may change what: changing anything needs `Permission.SenderReview`. The baseline can be changed only by someone with no group limit. A group's defaults can be changed by someone with no group limit, or whose limit includes that group. A client portal user can change nothing.
- Audit: reuse `AuditActions.SenderDecisionChanged`. A group change targets the group (`AuditTarget.For(group)`), and a baseline change targets `AuditTarget.Settings("Sender baseline")`. Changes record the status and the note.
- Copy follows Phase 1: statuses read Authorised, Unauthorised, Under review, Unreviewed. No em dashes. Meaningful variable names, even in tests.

## Review Focus

- A group-limited person typing another group's defaults URL, or the baseline URL: refused with the Access denied page, never a form.
- A group whose defaults page is open while the group is deleted: saving shows a message and does not throw.
- A baseline row for a service the catalogue no longer has: listed and resettable, never silently dropped.
- Two people changing the same baseline entry at once: one row, both succeed, the later write wins.
- A decision made at one level showing correctly on a domain page that has a conflicting group decision (Phase 1's resolver covers it; this phase must not break the domain page's "from the group" wording).

---

### Task 1: Baseline and group writes

**Files:**
- Modify: `src/DotMarc/Data/SenderDecisionService.cs`
- Test: `test/DotMarc.Tests/Data/SenderDecisionServiceTests.cs` (add to it)

**Interfaces:**
- Consumes: `SenderDecision`, `SenderServiceCatalog.All`, `AuditActor`, `AuditLog.Record`, `AuditChanges`, `AuditTarget.For(Group)` and `AuditTarget.Settings(string)`.
- Produces, on `SenderDecisionService` in namespace `DotMarc.Data`:
  - `Task SetBaselineAsync(DotMarcDbContext context, AuditActor actor, string serviceName, SenderDecisionStatus status, string? note, CancellationToken cancellationToken = default)`
  - `Task ResetBaselineAsync(DotMarcDbContext context, AuditActor actor, string serviceName, CancellationToken cancellationToken = default)`
  - `Task SetForGroupAsync(DotMarcDbContext context, AuditActor actor, int groupId, string serviceName, SenderDecisionStatus status, string? note, CancellationToken cancellationToken = default)`
  - `Task ResetForGroupAsync(DotMarcDbContext context, AuditActor actor, int groupId, string serviceName, CancellationToken cancellationToken = default)`
  - `Task<List<SenderDecision>> LoadBaselineAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)`
  - `Task<List<SenderDecision>> LoadForGroupAsync(DotMarcDbContext context, int groupId, CancellationToken cancellationToken = default)` returning the group's own decisions and the baseline

- [ ] **Step 1: Write the failing tests** (add to `SenderDecisionServiceTests`):
  - `SetBaselineAsync_CreatesABaselineDecision_AuditedAgainstTheSenderBaselineSettings` (scope Baseline, no owner ids, audit target type "Settings" and name "Sender baseline", a change from "Unreviewed")
  - `SetBaselineAsync_ChangesAndResets_AndAuditsEachStep`
  - `SetBaselineAsync_WithAnUnknownServiceName_Throws` (`ArgumentException`, "Mailchimpp" is not in the catalogue)
  - `SetBaselineAsync_WithNothingChanged_WritesNoAuditEntry`
  - `TwoPeopleSettingTheSameBaselineEntryAtOnce_LeaveOneRow_AndBothSucceed`
  - `SetForGroupAsync_CreatesAGroupDecision_AuditedAgainstTheGroup` (target type "Group", name the group's name)
  - `SetForGroupAsync_ForAnUnknownGroup_Throws` (`InvalidOperationException`)
  - `ResetForGroupAsync_DeletesOnlyThatGroupsDecision_AndDoesNothingWhenThereIsNone`
  - `LoadBaselineAsync_ReturnsOnlyBaselineRows`
  - `LoadForGroupAsync_ReturnsTheGroupsOwnDecisionsAndTheBaseline_AndNothingElse`
  - `AStoredDecisionForAServiceNoLongerInTheCatalogue_CanStillBeReset` (insert a baseline row with the key "Retired Service" directly, then `ResetBaselineAsync` removes it)
- [ ] **Step 2: Run** `dotnet test test/DotMarc.Tests --filter SenderDecisionServiceTests`. Expected: FAIL, methods missing.
- [ ] **Step 3: Implement.** Refactor the upsert in `SetAsync` into one private method that takes the scope, the owner ids, the audit target and the summary text, so the domain, group and baseline writers share one body, including the unique-violation retry. Service-name validation (catalogue names only) applies to the group and baseline setters, not to `ResetBaselineAsync` or `ResetForGroupAsync`. Summary texts: "Marked {service} as {status} in the sender baseline", "Marked {service} as {status} in {group}'s sender defaults", and "Reset {service} in the sender baseline" or "Reset {service} in {group}'s sender defaults".
- [ ] **Step 4: Run** the tests, including the Phase 1 ones in the same class. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Save baseline and group sender decisions"`

---

### Task 2: Who may change which level

**Files:**
- Create: `src/DotMarc/Reporting/Senders/SenderDecisionAccess.cs`
- Test: `test/DotMarc.Tests/Reporting/SenderDecisionAccessTests.cs`

**Interfaces:**
- Consumes: `ClientPortalGate.IsPortalUser`, `PortalData.ScopedGroupIds(ClaimsPrincipal)`, `PortalScope.CanPreview(ClaimsPrincipal, int)`.
- Produces: `public static class SenderDecisionAccess` in namespace `DotMarc.Reporting` with `bool MayChangeBaseline(ClaimsPrincipal user)` and `bool MayChangeGroup(ClaimsPrincipal user, int groupId)`. Both are about scope only. The `SenderReview` permission is checked separately, by policy.

- [ ] **Step 1: Write the failing tests** (build principals with the claim types the other portal and report access tests use):
  - `AnUnlimitedPerson_MayChangeTheBaseline_AndAnyGroup`
  - `AGroupLimitedPerson_MayNotChangeTheBaseline`
  - `AGroupLimitedPerson_MayChangeOnlyTheirOwnGroups`
  - `AClientPortalUser_MayChangeNothing` (portal claim set, with and without group scope)
- [ ] **Step 2: Run** `--filter SenderDecisionAccessTests`. Expected: FAIL.
- [ ] **Step 3: Implement** the two methods in terms of the helpers listed under Consumes.
- [ ] **Step 4: Run** the tests. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Decide who may change baseline and group sender decisions"`

---

### Task 3: The sender defaults page

**Files:**
- Create: `src/DotMarc/Components/Pages/SenderDefaults.razor`
- Modify: `src/DotMarc/Components/Pages/ManageGroups.razor` (a "Sender defaults" column and button), `src/DotMarc/Components/Layout/MainLayout.razor` (a "Sender baseline" menu item), `src/DotMarc/Components/Dialogs/SenderDecisionDialog.razor` only if its wording needs to name a level

**Interfaces:**
- Consumes: `SenderDecisionService` (Task 1), `SenderDecisionAccess` (Task 2), `SenderDecisionResolver.Resolve`, `SenderDecisionPresentation.StatusLabel` and `Provenance`, `SenderServiceCatalog.All`, `SenderDecisionDialog` and its result record, `AuditActorAccessor`.
- Produces: the page `SenderDefaults` with two routes, `/senders/baseline` and `/groups/{GroupId:int}/senders`, `[Authorize(Policy = "SenderReview")]`, and a nullable `[Parameter] public int? GroupId`.

- [ ] **Step 1: Build `SenderDefaults.razor`.**
  - On load, check scope with `SenderDecisionAccess`: no `GroupId` needs `MayChangeBaseline`, a `GroupId` needs `MayChangeGroup`. A refusal, or a group that doesn't exist, navigates to `/AccessDenied` the way `DomainDetail.razor` does.
  - Title "Sender baseline" or "Sender defaults for {group}", with one line of explanation: a baseline applies to every domain that uses the service, a group's defaults override it for that group's domains, and a domain's own decision overrides both.
  - A table of every catalogue service (catalogue order) followed by any stored decision whose service is no longer in the catalogue, labelled "{name} (no longer recognised)". Columns: Service, Decision (a status chip), Source, Note, and a Change button.
  - On the baseline page, Decision and Source describe the baseline row ("set here" or Unreviewed). On a group page they describe what the group's domains get: the group's own decision ("set for this group"), else the baseline's ("from the baseline"), else Unreviewed.
  - Change opens `SenderDecisionDialog` with the service name, the stored status and note for this level, `HasOwnDecision` meaning a row exists at this level, and on a group page the inherited text ("Currently Authorised, from the baseline."). Save calls the matching `Set...Async`, Reset calls the matching `Reset...Async`, then the list reloads and a snackbar confirms. A failure (`ArgumentException`, `InvalidOperationException`, `DbUpdateException`) shows the message in a snackbar and leaves the list as it was.
- [ ] **Step 2: Add the entry points.** In `ManageGroups.razor`, a "Sender defaults" header and a button per row inside `<AuthorizeView Policy="SenderReview">` that navigates to `/groups/{id}/senders`, shown only when `SenderDecisionAccess.MayChangeGroup` is true for that group. In `MainLayout.razor`, a "Sender baseline" item in the Manage menu inside `<AuthorizeView Policy="SenderReview">`, shown only when the person may change the baseline.
- [ ] **Step 3: Build** `dotnet build src/DotMarc`. Expected: no errors or new warnings.
- [ ] **Step 4: Verify in the browser.** Start a throwaway Postgres container and the app in demo mode, sign in as Demo Admin and check: the menu entry and the baseline page list every service; set Microsoft 365 to Authorised at the baseline; open a domain that uses it and see "Authorised, from the baseline"; set a group default of Unauthorised for the group that domain is in and see the domain change to "from the Aurora Retail group"; set a domain decision and see it override both; Reset at each level returns the domain to the level below; a stored decision for a retired name appears as "no longer recognised" (insert one with SQL). The demo's Demo Viewer lacks the permission, so the group-limited refusal is covered by the Task 2 tests and not seen here. Look at the screenshots, then stop the app and container.
- [ ] **Step 5: Commit** `git commit -m "Set sender defaults for every domain or for one group"`

---

### Task 4: Docs and a full run

**Files:**
- Modify: `website/docs/getting-started.mdx` (the decision section), `website/docs/permissions-and-access.mdx` if it needs a line on who may change the baseline

- [ ] **Step 1: Document** the baseline and group defaults: where to find them, the order they apply in (domain, then group, then baseline), that a baseline or group decision applies only to domains whose reports show that service, that groups that disagree resolve to the most cautious and the domain row says so, who may change which level, and that decisions on a service that has left the catalogue stay and are shown as no longer recognised. Replace the sentence saying these come in a later release. No em dashes.
- [ ] **Step 2: Run the full suite** `dotnet test test/DotMarc.Tests`. Expected: all PASS.
- [ ] **Step 3: Commit** `git commit -m "Document baseline and group sender defaults"`
