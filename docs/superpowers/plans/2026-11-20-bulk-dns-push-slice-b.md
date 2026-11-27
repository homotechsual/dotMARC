# Bulk DNS push, slice B: the UI and the demo provider

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A person can select domains on the dedicated Domain Inventory page, choose a target, review what will change for each domain, sign in once, watch the batch run and see a result per domain, all working on the demo instance with a fake provider. Dashboard is the action centre, not a bulk-selection entry point.

**Prerequisite:** Coordinate Tasks 7 and 8 with the [Inventory, filter and navigation implementation](2026-11-25-domain-inventory-filters-and-navigation.md). Inventory is `/domains`, with the `DomainInventory` route component and shared `DomainWorkspace`. Its `_rows` collection is filtered and sorted before grouping, contains distinct domain IDs, and is the selection source; grouped copies must share that identity. Add the batch action to the one page-level toolbar, never per-group filter controls. Do not add a temporary Dashboard selection flow.

**Architecture:** Slice A already stores, reviews, claims and runs a batch (`DnsPushBatchService`, `DnsPushBatchGate`, `DnsPushBatchCallback`, `DnsPushBatchRunner`, `DnsPushBatchWorker`) and exposes `/dns-push/{provider}/start-batch`. Slice B adds the pages and dialogs on top, stores each domain's review text on its batch row so the review page never re-plans, and registers a demo DNS push provider with demo lookups so the whole flow runs without a real DNS account. The page follows the batch by reading its stored state on a timer, which works on any replica.

**Tech Stack:** Blazor Server, MudBlazor, EF Core with Npgsql, xUnit with the Postgres fixture, Edge through playwright-edge for the browser check.

**Spec:** `docs/superpowers/specs/2026-11-20-bulk-dns-push-design.md`, with slice A in `docs/superpowers/plans/2026-11-20-bulk-dns-push-slice-a.md` and the roadmap in `docs/superpowers/plans/2026-11-20-v0-14-0-roadmap.md` (item 2).

## Global Constraints

* No em dashes in any prose, comments, docs or release text. One paragraph or list item per line in docs. No hard line wraps.
* Meaningful variable names everywhere, including throwaway test snippets.
* `TreatWarningsAsErrors` is on. The OpenAPI copy must be regenerated (`node scripts/update-openapi.mjs`) if the API surface changes, and nothing in slice B should change it.
* The first version pushes `dmarc`, `tlsrpt` and `mta-sts` only (`DnsPushBatchService.BulkTargets`). A batch has at most 50 domains (`DnsPushBatchLimits.MaxDomains`) and one provider. A review can be signed in to for 30 minutes.
* The target's policy decides who may start a batch: `IDnsChangeBuilder.RequiredPolicy` (`DomainsEdit` for `dmarc` and `tlsrpt`, `MtaStsManage` for `mta-sts`). Checking it only in the UI is not enough: the review page and every action on it check it too.
* A domain that would lose a CNAME delegation is unticked by default and needs an explicit tick on that domain's checkbox (decided in the roadmap). Select-all must not grant that consent, including when its warning is collapsed.
* The provider session never leaves memory and is never shown, logged or stored. No page, dialog or message in this slice may print a code, token, verifier or state value.
* Browser checks use Edge through playwright-edge only (never install Chrome), against the demo with the demo admin sign-in, and the app and its `dotmarc-verify` container are stopped afterwards.
* Each task is one commit. Tests are written first and watched failing.

## Review Focus

* A person without the target's policy (a Viewer, or an editor without `MtaStsManage` for `mta-sts`) must not see the Push action and must not be able to open or act on a review page by its URL. Expected: the action is absent, the page refuses and says why.
* A selection of 51 or more domains on one provider. Expected: a plain message to select at most 50, and no batch is created.
* A domain with no detected provider, or one whose provider has no configured push option. Expected: listed as "set up manually" with its reason and left out of every batch.
* A review left open past its 30 minute window, or opened by someone other than the person who started it. Expected: the first shows "expired" and offers to start again, the second shows the same not-found page as a missing batch, and neither can sign in.
* The sign-in popup blocked, or closed without finishing. Expected: a warning that names the pop-up blocker, the batch stays Planned and can be signed in to again, and the page never claims a push started when it did not.
* Every domain in a batch already correct. Expected: the review says so and the sign-in button is disabled.

---

### Task 1: Keep each domain's review with the batch

The review page must show additions, removals and lost delegations per domain without planning again (planning does live DNS lookups, up to 50 of them). Slice A stores only a fingerprint, so store the review itself.

**Files:**
- Modify: `src/DotMarc/DnsPush/DnsPushBatch.cs`, `src/DotMarc/DnsPush/DnsPushBatchService.cs`, `src/DotMarc/Data/DotMarcDbContext.cs`
- Create: `src/DotMarc/DnsPush/DnsPushBatchReviewJson.cs`, a migration `AddDnsPushBatchDomainReview` (generate with `dotnet ef migrations add`, then check the Designer and snapshot changed only by the new column)
- Test: `test/DotMarc.Tests/DnsPush/DnsPushBatchReviewJsonTests.cs`, additions to `DnsPushBatchServiceTests.cs`

**Interfaces:**
- Consumes: `DnsPushReview.From(DnsChangePlan)` (record with `ZoneName`, `Additions`, `Removals`, `LostDelegations`, `NothingToChange`), `DnsPushBatchService.CreateAsync` and its private `ReviewAsync`.
- Produces: `DnsPushBatchDomain.ReviewJson` (`string?`, a text column, set for `Planned` rows only), `static class DnsPushBatchReviewJson { string Write(DnsPushReview review); DnsPushReview? Read(string? json); }`, and `DnsPushBatchDomain.Included` false at creation when the review has lost delegations.

- [ ] **Step 1: Write the failing tests.**
  - `Write_ThenRead_GivesAnEqualReview`: a review with one addition, one removal and one lost delegation round trips with every field equal.
  - `Read_OfNullOrGarbage_IsNull`: `Read(null)`, `Read("")` and `Read("{not json")` all return null and never throw.
  - Service tests: `CreateAsync_StoresTheReviewForEveryPlannedDomain_AndNoneForTheOthers` (a refused, a nothing-to-change and a planned domain, and only the planned one has a `ReviewJson` that reads back); `CreateAsync_UntickCurrentlyDelegatedDomains` (a domain whose plan has a lost delegation is `Planned` with `Included == false`, and a normal one is `Included == true`; use a `ScriptedDnsChangeBuilder` change with `ExistingRecordType: "CNAME"` of kind `Replace`); `SetIncludedAsync_CanTickADomainThatWasUntickedForALostDelegation`.
- [ ] **Step 2: Run them.** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DnsPushBatchReviewJson|FullyQualifiedName~DnsPushBatchServiceTests"`. Expected: FAIL to compile (the type and column do not exist).
- [ ] **Step 3: Implement** the column, `DnsPushBatchReviewJson` with `System.Text.Json` (read must catch `JsonException`), the migration, and `ReviewAsync` setting `ReviewJson` and `Included = review.LostDelegations.Count == 0`.
- [ ] **Step 4: Run** the same filter, then `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DnsPush"`. Expected: all pass.
- [ ] **Step 5: Commit** `Keep each reviewed domain's changes with its batch`.

### Task 2: Group a selection by provider

**Files:**
- Create: `src/DotMarc/DnsPush/DnsPushSelection.cs`
- Test: `test/DotMarc.Tests/DnsPush/DnsPushSelectionTests.cs`

**Interfaces:**
- Consumes: `DetectedDnsProvider` and its `ToProviderKey()` extension (null for a provider dotMARC cannot push to), `DnsPushBatchLimits.MaxDomains`.
- Produces:
  - `record SelectedDomain(int Id, string Name, DetectedDnsProvider Provider)`
  - `enum DnsPushManualReason { NotDetected, NoPushOption }`
  - `record DnsPushManualDomain(SelectedDomain Domain, DnsPushManualReason Reason)`
  - `record DnsPushProviderGroup(string ProviderKey, IReadOnlyList<SelectedDomain> Domains) { bool OverLimit => Domains.Count > DnsPushBatchLimits.MaxDomains; }`
  - `record DnsPushSelectionGroups(IReadOnlyList<DnsPushProviderGroup> Batches, IReadOnlyList<DnsPushManualDomain> Manual)`
  - `static DnsPushSelectionGroups DnsPushSelection.Group(IReadOnlyList<SelectedDomain> selection, IReadOnlySet<string> configuredProviderKeys)`

- [ ] **Step 1: Write the failing tests.**
  - `ADomainPerProvider_BecomesOneGroupPerProvider_InSelectionOrder`
  - `ADomainWithNoDetectedProvider_IsManual_NotDetected` (`NotChecked` and `Unknown` both)
  - `AProviderDotMarcCannotPushTo_IsManual_NoPushOption` (for example `GoDaddy`)
  - `AKnownProviderThatIsNotConfigured_IsManual_NoPushOption`
  - `ADuplicateInTheSelection_IsCountedOnce`
  - `AGroupOfFiftyOne_IsOverTheLimit_AndAGroupOfFiftyIsNot`
  - `AnEmptySelection_GivesNoGroupsAndNoManual`
- [ ] **Step 2: Run.** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DnsPushSelectionTests"`. Expected: FAIL (type missing).
- [ ] **Step 3: Implement** `DnsPushSelection.Group` as a pure function.
- [ ] **Step 4: Run.** Expected: all 7 pass.
- [ ] **Step 5: Commit** `Group a selection of domains by DNS provider`.

### Task 3: Read a batch for the page, and say what each status means

**Files:**
- Modify: `src/DotMarc/DnsPush/DnsPushBatchService.cs`
- Create: `src/DotMarc/DnsPush/DnsPushBatchPresentation.cs`
- Test: additions to `DnsPushBatchServiceTests.cs`, `test/DotMarc.Tests/DnsPush/DnsPushBatchPresentationTests.cs`

**Interfaces:**
- Consumes: `DnsPushBatch`, `DnsPushBatchDomain`, `DashboardPreferencesStore.UserKeyOf(AuditActor)`.
- Produces:
  - `Task<DnsPushBatch> DnsPushBatchService.GetAsync(DotMarcDbContext context, AuditActor actor, Guid batchId, CancellationToken cancellationToken = default)`: no tracking, rows included, throws `DnsPushBatchException` with the same text for a missing batch and for someone else's batch.
  - `static class DnsPushBatchPresentation { string Label(DnsPushBatchDomainStatus status); Color Color(DnsPushBatchDomainStatus status); string Explain(DnsPushBatchDomainStatus status, string? detail); string Label(DnsPushBatchStatus status); DnsPushBatchCounts Count(DnsPushBatch batch); }` and `record DnsPushBatchCounts(int WillChange, int AlreadyCorrect, int NeedAttention, int Included, int Pushed, int Failed, int Skipped, int NotAttempted)`.
  - Labels: Pushed "Pushed", NothingToChange "Already correct", Refused "Needs attention", ChangedSinceReview "Changed since you reviewed it", ZoneNotFound "Zone not found", Failed "Failed", FailedAfterChange "Failed after changing DNS", NotAttempted "Not attempted", Planned "Waiting". `Explain` turns the refusal flags (`unmergeable`, `not-found`, `error`, `zone-not-found`) into one plain sentence each, passes a provider message through unchanged, and never returns null.

- [ ] **Step 1: Write the failing tests.**
  - `GetAsync_ReturnsTheBatchWithItsRows_ForItsOwner`
  - `GetAsync_GivesTheSameMessage_ForAMissingBatchAndSomeoneElsesBatch`
  - `GetAsync_RefusesAnApiKeyActor`
  - `Count_AddsUpEveryStatus_AndPushedPlusFailedPlusSkippedPlusNotAttemptedCoversTheIncludedRows` (a four-row batch with one of each)
  - `Label_IsNonEmptyAndDistinct_ForEveryDomainStatus` (iterate `Enum.GetValues`)
  - `Explain_NeverReturnsAnEmptyString_ForAnyStatusOrFlag`
- [ ] **Step 2: Run.** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DnsPushBatchPresentation|FullyQualifiedName~DnsPushBatchServiceTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** both.
- [ ] **Step 4: Run** the DnsPush filter. Expected: all pass.
- [ ] **Step 5: Commit** `Read a batch for its review and results pages`.

### Task 4: A demo DNS push provider

Today the demo registers only the three real providers and the real detector and lookups, which would call real DNS for made-up domains. The demo needs its own so a push can run end to end.

**Files:**
- Create: `src/DotMarc/Demo/DemoDnsPushProvider.cs`, `src/DotMarc/Demo/DemoDnsLookups.cs`
- Modify: `src/DotMarc/Program.cs` (demo registrations beside the real ones, in the same `if (demoOptions.Enabled)` style as `DemoPsaProvider`), `src/DotMarc/Demo/DemoDataGenerator.cs` and its seeding path as needed
- Test: `test/DotMarc.Tests/Demo/DemoDnsPushProviderTests.cs`, additions to the demo generator/seeding tests

**Interfaces:**
- Consumes: `IDnsPushProvider`, `IDnsPushSession`, `DnsPushSessionResult`, `DnsPushResult`, `IDnsProviderDetector`, `IDmarcTxtLookup`, `ITlsrptTxtLookup` (read their definitions before writing the demo versions), `DetectedDnsProvider.ToProviderKey()`.
- Produces:
  - `DemoDnsPushProvider(string providerKey)`: `IsConfiguredAsync` is true, `BuildAuthorizationUrlAsync(state, challenge, redirectUri)` returns the callback URL itself with `code=demo-code` and the state, so the "sign-in" is a redirect straight back and shows no real provider page, `ExchangeAsync` returns a session, `PushAsync` waits about 400 ms (so progress is visible) and returns `Pushed` for each domain except any whose zone name contains `broken` (returns `ProviderError` with "The demo provider rejected this record."), which gives the demo a failure to show.
  - `DemoDnsProviderDetector` (reads the stored `Domain.DnsProvider` for the domain name and returns that provider with the zone equal to the domain name; a name that is not in the database is `Unknown`), `DemoDmarcTxtLookup` and `DemoTlsrptTxtLookup` (no existing record for ordinary demo domains). For `already-` domains, DMARC returns `v=DMARC1; p=none; {DmarcRuaMerge.RuaTag(mailbox, additionalAddress)}`, using the configured Graph mailbox and DNS settings just as `DmarcChangeBuilder` does; TLSRPT returns `v=TLSRPTv1; rua=mailto:{tlsrptMailbox}`, using `GraphOptions.TlsrptMailboxAddress`. These are distinct protocol records, not one shared hard-coded value. Seed nonempty demo mailbox settings so both targets can plan.
  - Deterministic demo fixtures: `push-create.example`, `already-correct.example` and `broken-push.example`, all assigned `DetectedDnsProvider.Cloudflare`, plus `manual-push.example` assigned an unsupported provider such as GoDaddy. Include them in a demo-admin-accessible group. The first plans changes and succeeds, the second is already correct for DMARC and TLSRPT, the third plans changes then fails on each attempt with the documented demo rejection, and the fourth is listed for manual setup. Add them through the normal generator/seeder so a fresh or reset demo has them; update exact-count expectations affected by those additions.
  - In demo mode the three real push providers and the real detector and lookups are not registered, and one `DemoDnsPushProvider` per key `cloudflare`, `azure-dns` and `google-cloud-dns` is.

- [ ] **Step 1: Write the failing tests.** `Exchange_GivesASession_AndPushReturnsPushed`, `PushToAZoneNamedBroken_ReturnsAProviderError_WithNoSessionOrTokenInTheMessage`, `TheAuthorizationUrl_ReturnsToTheCallbackWithTheStateUntouched` (parse the URL, check the path is `/dns-push/{key}/callback` and `state` equals the input), `TheDetector_ReturnsTheStoredProviderAndTheDomainAsItsZone` (Postgres fixture, a domain stored as Cloudflare), `TheDetector_ReturnsUnknownForAnUnstoredName`. Build real DMARC and TLSRPT plans against the demo lookups and assert `DnsPushReview.From(plan).NothingToChange` for `already-correct.example`, including a configured additional DMARC rua address; assert changes for `push-create.example`. Generator/seeding tests assert the four named fixtures, provider assignments, accessible group membership and required mailbox settings exist after seed/reset.
- [ ] **Step 2: Run.** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DemoDns"`. Expected: FAIL.
- [ ] **Step 3: Implement** the classes and the demo-only registrations. If `MtaStsChangeBuilder` needs lookups beyond these (check its constructor), leave `mta-sts` out of the demo and record that as a ledger ruling rather than adding more fakes.
- [ ] **Step 4: Run** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~Demo"`. Expected: all pass, with existing seeding tests updated only where the new deterministic fixtures legitimately change expectations.
- [ ] **Step 5: Commit** `Add a demo DNS push provider so a push can be shown`.

### Task 5: The review page

**Files:**
- Create: `src/DotMarc/Components/Pages/DnsPushBatchPage.razor`, `src/DotMarc/Components/Shared/DnsPushBatchReviewList.razor`
- Modify: `src/DotMarc/Program.cs` (the `start-batch` endpoint), `src/DotMarc/Components/App.razor` only if the opener needs a change
- Test: `test/DotMarc.Tests/Components/DnsPushBatchPageTests.cs` (bUnit if the repo already uses it for pages, otherwise pure tests of the extracted view model below), `DnsPushBatchStartTests.cs`

**Interfaces:**
- Consumes: `DnsPushBatchService.GetAsync/SetIncludedAsync`, `DnsPushBatchPresentation`, `DnsPushBatchReviewJson.Read`, `DnsRecordsTable`, the existing `dotMarcOpenDnsPush(url, dotNetRef)` JS opener and its three callbacks (`OnDnsPushWaiting`, `OnDnsPushResult(string outcome)`, `OnDnsPushPopupBlocked`).
- Produces:
  - Route `@page "/dns-push/batch/{BatchId:guid}"`, `@attribute [Authorize]`, and a check on load that the person holds the batch target's `RequiredPolicy` (otherwise the page shows the same not-found text as a missing batch).
  - The page shows a summary line ("38 will change, 9 already correct, 3 need attention"), one collapsed row per domain (name, status chip, a short "adds 1, replaces 1" line), expanding to the `DnsPushBatchReviewJson.Read` review using `DnsRecordsTable` for additions and removals and a red alert for lost delegations. A checkbox per `Planned` domain, a "Select all with changes" toggle, and a **Sign in and push** button that saves the ticked set through `SetIncludedAsync` and then opens `/dns-push/{provider}/start-batch?batchId={id}` in the popup.
  - Select-all includes only `Planned` rows with a readable review and no lost delegations; it preserves a delegated row's current explicit selection but never turns an unticked delegated row on. Only that row's checkbox can opt it in; unticking it withdraws consent. Bulk clearing can clear all selections. A missing/unreadable stored review blocks signing in and explains that a fresh review is needed rather than treating unknown delegation state as safe.
  - The button is disabled, with the reason shown, when nothing is ticked, when the batch is not `Planned`, when it has expired, or when a required stored review is unreadable.
  - `start-batch` takes `string? pushId` like `/start` and stores it in the state's `pushId` (the page's id, not the batch id), because the opener only accepts a result whose `pushId` equals the one it generated. Today the endpoint stores the batch id there, so the popup's result would be ignored.
  - `OnDnsPushResult("batch-started")` switches the page to the progress view (Task 6). `cancelled` and `invalid` show a warning and leave the review usable. `error` says the sign-in was rejected and that a new review is needed, since the batch is no longer `Planned`.

- [ ] **Step 1: Write the failing tests.**
  - A pure view model, `DnsPushBatchReviewModel` (in `src/DotMarc/Components/Shared/`), built from a batch: `CanSignIn` is false with each reason above (`NothingTicked`, `NotPlanned`, `Expired`, unreadable review), true otherwise, and `TickedDomainIds` matches `Included`. Test each case. `SelectAllWithChanges` selects ordinary `Planned` rows, leaves delegated rows unticked with warnings collapsed or expanded, preserves an explicitly ticked delegated row, does not reselect it after explicit unticking, and excludes non-planned or unreadable-review rows.
  - `TheStartedBatchEndpoint_PutsThePagesPushIdInTheState` through the extracted state-building helper: the protected state's `PushId` equals the supplied GUID and `BatchId` equals the batch id, and a non-GUID `pushId` is dropped.
  - If bUnit is already a test dependency: the page renders the not-found text for another person's batch and shows no Sign in button.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** the model, the helper (extract the state-building out of the endpoint lambda into a static method so it is testable), the page and the list component, following the style of `DomainDetail.razor`'s push popup code for the JS callbacks.
- [ ] **Step 4: Run** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DnsPush|FullyQualifiedName~Components"`. Expected: all pass.
- [ ] **Step 5: Commit** `Review a batch of DNS pushes before signing in`.

### Task 6: Progress, results and retrying the failures

**Files:**
- Modify: `src/DotMarc/Components/Pages/DnsPushBatchPage.razor`, `src/DotMarc/Components/Shared/DnsPushBatchReviewModel.cs` (from Task 5)
- Create: `src/DotMarc/Components/Shared/DnsPushBatchResults.razor`
- Test: additions to `DnsPushBatchPageTests.cs`

**Interfaces:**
- Consumes: `DnsPushBatchService.GetAsync`, `DnsPushBatchPresentation.Count/Label/Color/Explain`, `DnsPushBatchLimits`.
- Produces:
  - When the batch is `Running`, `Completed` or `Interrupted`, the page shows a progress bar over the included domains, a results table (domain, status chip, explanation, time) and re-reads the batch every two seconds until it is `Completed`, `Interrupted` or `Expired`, then stops. The timer is disposed with the page.
  - `Interrupted` shows a plain notice: "dotMARC stopped, or the sign-in ended, before this batch finished. Domains marked Not attempted were not touched. Start a new batch for them." A `FailedAfterChange` row gets a warning that the zone may have changed and should be checked.
  - A **Retry the failed domains** button, shown when any row is `Failed`, `ChangedSinceReview` or `NotAttempted`, which creates a new batch for those domain ids (same provider and target) through `DnsPushBatchService.CreateAsync` and navigates to its review page. It states that a new sign-in will be needed. `ZoneNotFound` and `FailedAfterChange` rows are not retried automatically.
  - `DnsPushBatchReviewModel.RetryDomainIds` (pure) returns those ids in position order.

- [ ] **Step 1: Write the failing tests.** `RetryDomainIds_AreTheFailedChangedAndNotAttemptedRows_InPositionOrder`, `RetryDomainIds_AreEmpty_WhenEverythingWasPushed`, `ProgressFraction_CountsFinishedIncludedRowsOverIncludedRows` (and is 0 for a batch with nothing included, never NaN), `PollingShouldStop_OnlyForCompletedInterruptedAndExpired`.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** the model members, the results component, the timer and the retry action.
- [ ] **Step 4: Run** the DnsPush and Components filter. Expected: all pass.
- [ ] **Step 5: Commit** `Show a batch of DNS pushes running and what happened to each domain`.

### Task 7: Start a batch from Domain Inventory

**Files:**
- Create: `src/DotMarc/Components/Dialogs/StartDnsPushBatchDialog.razor`, `src/DotMarc/Components/Shared/DnsPushSelectionBar.razor`
- Modify: `src/DotMarc/Components/Shared/DomainWorkspace.razor` and its code-behind, reached through `Pages/DomainInventory.razor` at `/domains`. Reuse the distinct filtered `_rows` IDs and server-side `_domainsById` metadata; expose `DnsProvider` in the chooser's read model without loading domains again per row.
- Test: `test/DotMarc.Tests/DnsPush/DnsPushStartModelTests.cs`, additions to the Inventory row and permission tests

**Interfaces:**
- Consumes: `DnsPushSelection.Group`, `IEnumerable<IDnsPushProvider>.FindConfiguredAsync`, `IEnumerable<IDnsChangeBuilder>`, `IAuthorizationService`, `DnsPushBatchService.CreateAsync`, `DnsPushBatchService.BulkTargets`.
- Produces:
  - The Inventory row's `DnsProvider` (`DetectedDnsProvider`).
  - Inventory keeps one page-level `HashSet<int> _selectedDomainIds` and renders a checkbox in each row's first cell and a header "select all shown" checkbox for the rows the current filters show. It does not use `MudTable.MultiSelection`, because the By group view builds several tables and each would own a separate selection. A selection survives sorting and switching between All domains and By group. In accordance with the 25 November Inventory plan, filtering or access changes remove selected IDs that are no longer visible/authorised, with visible feedback; select-all counts distinct current matches once. Clicking a checkbox must not trigger the domain link or a management control. Dashboard links to Inventory rather than duplicating selection controls.
  - `DnsPushSelectionBar` (shown only when something is selected and `AvailableTargets` contains at least one authorised bulk target): "N selected", **Push DNS records**, **Clear**. Evaluate each builder's `RequiredPolicy`; do not require `DomainsEdit` as an extra umbrella permission. An `MtaStsManage`-only user can open the chooser for `mta-sts`.
  - `StartDnsPushBatchDialog` takes the selected `SelectedDomain` list. It lets the person choose a target from `BulkTargets`, offering only the targets whose `RequiredPolicy` they hold. It shows the grouping from `DnsPushSelection.Group` (a card per provider with its count and a **Review** button, and a "Set up manually" list with each reason) and says plainly when a group is over 50. **Review** calls `CreateAsync` for that group, shows a spinner while the live lookups run, then navigates to `/dns-push/batch/{id}`. A `DnsPushBatchException` message is shown as the dialog's error.
  - A pure `DnsPushStartModel` with `AvailableTargets(IReadOnlySet<string> heldPolicies, IEnumerable<IDnsChangeBuilder>)`, which the dialog uses.

- [ ] **Step 1: Write the failing tests.** `AvailableTargets_OffersDmarcAndTlsrpt_ForAnEditorAndNothingForAViewer`, `AvailableTargets_OffersMtaSts_OnlyWithMtaStsManage`, `AvailableTargets_NeverOffersSpfDkimOrDmarcAuth`, `TheInventoryRow_CarriesTheDomainsDnsProvider`. Verify selection-bar/chooser access for `MtaStsManage` without `DomainsEdit` (bar visible, only `mta-sts` available), `DomainsEdit` without `MtaStsManage` (only DMARC/TLSRPT), both, and neither. Assert the same target policies on review and start actions, not merely UI visibility.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** the model, the row field, the bar, the dialog and the Inventory wiring after the prerequisite Inventory contract is settled.
- [ ] **Step 4: Run** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DnsPush|FullyQualifiedName~Dashboard"`. Expected: all pass.
- [ ] **Step 5: Commit** `Start a batch of DNS pushes from Domain Inventory`.

### Task 8: Preserve management controls in the Inventory workflow

**Files:**
- Modify: `src/DotMarc/Components/Shared/DomainWorkspace.razor` and its code-behind; the old `ManageDomains.razor` has been replaced by Inventory.
- Test: additions to `test/DotMarc.Tests/Components/DomainInventoryTests.cs` and the Inventory browser checks

**Interfaces:**
- Consumes: `DnsPushSelectionBar`, `StartDnsPushBatchDialog`, `SelectedDomain` (Tasks 2 and 7).
- Produces: one authoritative inventory selection flow, accessible to read-only inventory viewers without granting edit actions. Preserve Inventory's existing management columns and handler-level policy/scope checks. Do not reintroduce a separate Manage domains table or selection state.

- [ ] **Step 1: Write the failing tests** for management-to-Inventory navigation, preservation of read-only access and edit-only actions, and clicking a selection checkbox without invoking row navigation or another control.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.** Reuse the Task 7 flow unchanged and preserve the coordinated Inventory architecture rather than creating another selection surface.
- [ ] **Step 4: Run** the DnsPush and Domains filters. Expected: all pass.
- [ ] **Step 5: Commit** `Preserve domain management access around Inventory bulk selection`.

### Task 9: Check it in the browser, then the docs and notes

**Files:**
- Modify: the existing DNS push docs page under `website/docs` (find it with a search for "Push to DNS" or "dns-push"), the 0.14 release notes file if one exists
- No test file: this task is the browser check and writing

**Interfaces:**
- Consumes: everything above, with the demo running through the project's verification recipe (docker `dotmarc-verify` Postgres, the demo admin sign-in).
- Produces: the browser check recorded in the commit message, and docs for the person using it.

- [ ] **Step 1: Run the freshly seeded/reset demo and check, in Edge.** Sign in as the demo admin. Navigate from Dashboard to Inventory and select `push-create.example`, `already-correct.example`, `broken-push.example` and `manual-push.example`. Open Push DNS records, pick `dmarc`, and confirm: the manual domain is listed under "Set up manually", the create domain shows additions, the already-correct domain shows no changes, and the broken domain is reviewed normally. Sign in (the popup should return at once), and watch the create domain become Pushed and the broken domain become Failed. Retry creates a fresh review for only the broken domain and requires a new sign-in; its deterministic rejection remains visible on retry rather than falsely becoming successful. Repeat the already-correct check with `tlsrpt`. Sign in as a demo Viewer and confirm Inventory remains readable but has no push bar and the review URL shows the not-found page. Also exercise an `MtaStsManage`-only user at the chooser and real target authorisation boundary. Take a screenshot of the review and of the results and look at them.
- [ ] **Step 2: Check** grouped Inventory selection, explicit unticking and the delegation/select-all safeguard with a deterministic delegated test fixture; verify management navigation reaches the same Inventory flow and a reload on the results page still shows results.
- [ ] **Step 3: Write the docs.** A "Push to several domains at once" section in the DNS push docs: who can, which targets, the 50 domain limit, that a selection is split by provider and unsupported domains are listed to set up manually, what the review shows, that a plan that changed after the review is skipped and reported, what each result means, and what a retry does. Write it for someone running the app, in plain sentences, with no em dashes and one paragraph per line. Add the release-note line to the 0.14 notes if the notes file exists.
- [ ] **Step 4: Stop the app and remove the `dotmarc-verify` container.** Run the full suite to a file and read the tail. Expected: all pass, 0 failed.
- [ ] **Step 5: Commit** `Document pushing DNS records to several domains`.
