# Domain Inventory, unified filters and return navigation

**Status:** implemented on `develop`, with validation recorded below. The action-centre queues/chart and bulk-push UI are still separate slices.

**Goal:** make `/domains` the full Domain Inventory for every authorised viewer, move the Dashboard's full table there, and provide one page-level filter UI and reliable return navigation. Establish the Inventory contract that bulk DNS push slice B can build on.

**User clarification (25 November):** collapse all filters into one UI element. Filters affect every group, so By group must show exactly one set of filters, including the existing Group and Tag controls. Grouping changes presentation, not filtering scope.

**References:** [0.14 roadmap](2026-11-20-v0-14-0-roadmap.md), [Dashboard and management spec, section 7](../specs/2026-11-20-domain-baselines-security-score-and-dashboard-design.md#7-dashboard-and-management-workflow), [return-to-list spec](../specs/2026-11-20-return-to-list-position-design.md), [bulk DNS push slice B](2026-11-20-bulk-dns-push-slice-b.md).

## 1. Scope and implementation decisions

### Routes and navigation

| Route | Responsibility | Access |
| --- | --- | --- |
| `/dashboard` | Action centre, summaries and operational context; no full inventory table when the coordinated transition is complete | `DomainsView` |
| `/domains` | Domain Inventory, All domains or By group, health and management controls in one list | `DomainsView`, with existing individual write policies |
| `/domains/{name}` | Existing domain detail; Back returns to the originating list when available | Existing policy |
| `/domains/import` | Existing import workflow, linked from Inventory | Existing policy |
| `/groups` | Group management only after tag extraction | Existing group-management policies |
| `/tags` | Dedicated tag management | Existing tag-management policies |

- Reuse `/domains`, currently Manage domains, rather than create a competing `/inventory` route. Rename its component to `DomainInventory.razor` when merging the list responsibilities. Existing `/domains` bookmarks remain useful.
- Inventory is a primary navigation destination beside Dashboard, visible under `DomainsView`. Remove the duplicate Manage > Domains destination. Keep Add domain, Import and appropriate management actions discoverable in Inventory.
- The unified list must retain both the existing management surface and the Dashboard's monitoring information. Do not retain a second full management table behind a different tab, route or permission.
- Keep health columns readable by default. Put additional management information/actions in a single page-level expandable management section or column toggle, not another independently filtered table. Resolve exact column widths with a narrow-screen prototype before merging.
- Groups and Tags get separate Manage entries, each visible when at least one of its existing operations is allowed. Do not assume a `TagsWrite` or `GroupsWrite` policy exists; compose the actual Add/Edit/Delete policies as the current `GroupsOrTagsWrite` policy does.
- Existing Dashboard summary/operational content remains until the action-centre implementation replaces it. The table move and usable Inventory navigation land together; do not remove the only working domain list in an intermediate commit.

### Boundaries

- Include the table move, management preservation, Tags extraction, unified filtering, saved-default migration, list restoration and the navigation/deep links required by that move.
- Specify the interface to the action-centre work; do not implement new problem-priority rules, seven-day investigation queries or charts in this slice.
- Do not implement bulk push UI here. Provide a stable filtered row set and identity model for slice B, without a temporary Dashboard selection flow.
- Keep `IsMonitored` and current health/DMARC-target semantics. No secure score, purpose presets, baseline profiles or domain-age rules.
- This is a domain-workflow filter overhaul, not an instruction to rewrite unrelated Alerts, Logs or PSA tables.

## 2. One filter UI, one state, one domain set

### UI contract

1. A page-level toolbar contains All domains / By group, **Filters** and Save/Clear my default. It is rendered once, outside all group/table loops.
2. Filters is one accessible disclosure with one responsive panel. It is collapsed initially unless a migrated preference or explicit deep link opens it. Use the existing MudBlazor controls and application styling.
3. Every filter control lives in that panel: Group, Tag, domain search, report status, minimum pass rate, last-report search, monitored, DMARC target outcome, DMARC, MTA-STS and DKIM; preserve management filters for PSA company overrides and minimum report count as well.
4. Remove the separate Group/Tag dropdown row and every table-header filter row/toggle. No duplicated search input, filter panel, or per-group filter controls. Group tables contain headings, columns and rows only.
5. A compact, non-editable active-filter summary and `Filters (N)` remain visible when collapsed. The count counts criteria, not matching domains or selected values; disclosure state is not a criterion. Clear all is one action within the panel. Collapsing never clears or disables a filter.
6. Preserve current single Group and single Tag matching. Multiple selected criteria combine with AND. Do not introduce multi-select semantics in this refactor.
7. Changes apply immediately, as today; there is no separate draft/Apply state. Debounce text-triggered reloads if needed and discard superseded asynchronous results so a slower request cannot replace newer filters.
8. The panel stacks on narrow screens, has visible labels and keyboard focus, and exposes expanded state through its disclosure button. Do not hide active criteria in mobile-only controls.
9. Labels distinguish a viewer's access scope from their chosen Group filter. A one-group viewer's locked scope is not a removable filter. Clear all cannot expand access.

### Data flow and grouping

```text
Current authenticated access scope
    -> authorised domains and filter option data
    -> one normalised filter state
    -> one filtered set of distinct domain IDs
    -> one shared sort
    -> All domains OR By group presentation
```

- Apply the predicate before `DashboardGrouping.Group`, not separately inside each `MudTable.Filter`. This removes empty group sections when their domains do not match a global filter.
- Preserve existing grouping semantics: a domain belonging to two visible groups appears in both sections. Unique-domain totals and future bulk selection count it once. Group headings may count their own displayed rows.
- Only authorised group names may appear. A domain's hidden memberships must not become headings, filter options or return-state metadata. Unscoped users retain No group; scoped users never gain unassigned/out-of-scope domains through it.
- Selecting Group A limits the displayed group sections to A, matching current behaviour. With All my groups, matching domains may appear under each authorised membership.
- All domains / By group preserves every active criterion. The same sort column/direction applies to every group. Sortable column headers remain, but changing one updates the page's common sort state, not one table's private state.
- Reuse `DashboardGrouping`, `DashboardGroupChoice`, current status presentation helpers and `DmarcTargetCheck`. Extract the current row predicate into a testable domain-list filter helper instead of adding a second interpretation.
- Target filters remain monitored-only and must preserve Baselines count/list agreement. Below, checking and meets are not interchangeable with unchecked, unmonitored or no-target domains.
- Preserve current null pass-rate and last-report matching semantics in this slice; name them explicitly in regression tests. Do not silently change health semantics during a UI move.
- Build filter options from authorised data, not only the already-filtered rows, so selecting one value does not make the other choices disappear. Existing globally managed tag names retain their current visibility; no domain counts or membership outside access scope may leak.
- Dashboard adopts the same single filter panel and shared predicate for applicable domain criteria. There is one panel per page, not one for each queue/chart/group. Global polling/PSA diagnostics are not filtered as if they were domain data; label global information clearly and retain existing permission gates.
- In the coordinated action-centre work, every domain-scoped count, queue and chart must use its page's common filtered domain IDs before applying its own documented time window or queue condition. Global operational counts remain explicitly labelled. Inventory and Dashboard have separate current page state; explicit drill-through carries filters rather than relying on hidden cross-page coupling.

## 3. Preserve management behaviour and access

- Inventory admission changes from the current `DomainsWrite` management-page gate to `DomainsView`. This does not grant writes. Audit every handler moved from `ManageDomains.razor`, not just visible buttons: create, delete, group/tag assignment, PSA override, monitored toggle and reorder must re-check their specific policy and domain scope at execution time.
- Preserve validation, duplicate handling, audit events, PSA directory failure messaging, group-scope restrictions and confirmation dialogs by reusing the existing management services.
- Preserve Add/Import, group/tag assignment, per-PSA company overrides, monitored state, report counts, last report, domain deletion and persisted manual ordering. Do not drop a control because it is absent from the old Dashboard row.
- Keep the inventory row projection distinct from the tracked `Domain` entity. Extend/reuse the existing read models rather than loading PSA directories or report bodies once per row.
- A Viewer gets useful read-only group/tag and monitoring information, no mutation controls, and cannot mutate by invoking a stale circuit action.
- Persisted manual domain order and temporary column sorting are different concepts. Enable drag reorder only in All domains with no active column sort and no domain filters; otherwise explain why it is unavailable. Reuse the current reorder service and test the complete visible ordering before any write. Do not attempt to reorder duplicates in By group.
- Domain names are native links for normal browser/new-tab behaviour. Put assignment, selection and other interactive cells outside the navigation target.
- In slice B, selection is by domain ID across repeated group rows. A filter/scope change removes hidden/unauthorised IDs from the selection with visible feedback; Select all means the current distinct filtered domain set, never unseen rows. This slice exposes the row IDs but does not add batch actions.

## 4. Saved defaults and migration

Current `DashboardPreferences` stores view, Group, Tag, disclosure state and most column criteria in `UserDashboardPreferences.SettingsJson`. It omits free text, target outcome, sort and the explicit All my groups state. Do not lose existing saved choices or perpetuate those omissions accidentally.

- Use the existing per-user JSON row, with a versioned envelope holding separate Inventory and Dashboard defaults. No new database table is necessary. Retain `DashboardPreferencesStore.UserKeyOf` identity semantics for batch ownership; a list rename must not change who owns an existing batch.
- Recognise legacy JSON and map its existing fields to Inventory defaults. Seed applicable Dashboard domain criteria from the same legacy values so the transition does not silently widen a person's saved Dashboard scope. Grouped view belongs to Inventory only.
- Migration is read-compatible and idempotent: do not overwrite existing new-format defaults on later reads. Upgrade on an explicit successful save/clear, preserving the other page's settings.
- Persist the target outcome and explicit All my groups choice in the new format, along with supported structured criteria, view, disclosure state and shared sort. Do not persist domain/last-report free-text searches as defaults; they remain session return-state only. Preserve existing legacy exclusion of free text.
- Save and Clear my default affect only the current page. Clear all filters affects current criteria, not saved defaults, view mode or sort. Neither operation changes access scope.
- Revalidate saved group/tag IDs, recognised enum values and numeric bounds against current access/data. Explain discarded inaccessible/deleted selections with a concise notice. Do not guess a broader selection silently.
- Preserve current scoped-user fresh-visit behaviour: one group is fixed; a multi-group user starts in their existing default/first authorised group unless an explicit saved All my groups choice or deep link says otherwise. Do not conflate first-group fallback with no access.
- Unknown future versions and malformed JSON must not be overwritten as if successfully migrated. Log a diagnostic without sensitive stored content and notify the user that defaults could not be loaded. A current load failure must not display a success message.

## 5. Deep links, Back and list position

### Explicit links

- Canonical target drill-through becomes `/domains?target=below`, `checking` or `meets`. It clears unrelated saved/default criteria and spans all authorised groups, exactly as today's Baselines links do.
- Update `BaselinesDomain.razor` and tests to generate the new destination. Preserve old `/dashboard?target=...` links by forwarding recognised target queries to Inventory with replacement history. Do not redirect ordinary `/dashboard` visits or discard its existing `dnsPush=invalid` feedback.
- Use a small allowlisted query codec for Inventory domain criteria, view and sort. It is the same normalised state used by filtering, not another predicate. Unknown or invalid query values produce a visible validation notice and cannot bypass scope. Return URLs accept only recognised same-app list routes; never navigate to arbitrary supplied URLs.
- When a URL explicitly supplies filters, use a fresh base plus those filters, not a merge with unrelated saved criteria. Preserve Back/Forward through query changes without a new history entry on each keystroke.
- Future Dashboard drill-through links carry applicable criteria to Inventory. They must match the count/meaning they were opened from; if a queue needs a new criterion, implement that criterion and its tests in the action-centre slice rather than silently dropping it.

### Transient return state

- Spike the installed MudTable sort API first: demonstrate restoring column and direction before rows are displayed, including two grouped tables. Prefer a single externally sorted collection with headers reflecting that state if independently controlled MudTable sorts cannot stay in sync. Do not use timed DOM clicks.
- Store versioned list state and scroll in `sessionStorage`, keyed by authenticated user and a per-history-entry token, with a 30-minute expiry. Include complete filters, view, sort, disclosure state and source route; never tokens or provider credentials. Keep separate entries for Inventory and Dashboard.
- Distinguish return navigation from a fresh menu/bookmark visit with explicit history-entry markers. Merely finding recent cached state is insufficient. A fresh visit starts from URL/defaults at the top; an actual return can restore the matching entry.
- Precedence: on a matching Back return, that entry's transient state wins; on a fresh visit, explicit URL filters win over page defaults, which win over initial defaults. Revalidate access after restoration. Do not let a cached state override a newly followed target link.
- Save on in-app location changes and browser lifecycle events where appropriate; restore after data and rows have rendered, clamp scroll to the current document height and perform one restoration. No fixed sleeps or JS interop during prerender.
- Inventory and Dashboard links into a domain record the allowed origin and return marker. Domain detail's Back uses history only when its matching previous same-app list entry is known; otherwise navigate to `/domains`. It must never take someone who arrived directly from an alert/email outside the app.
- Browser Back and the page's Back both restore the list. New-tab domain links have no trusted origin and use the Inventory fallback. Keep return context when changing tabs on Domain detail.
- Clear user-specific transient memory on sign-out/persona change; access changes are rechecked regardless. Corrupt/expired/unavailable session storage must not break navigation: surface unavailable restoration once using the normal UI diagnostic path and use a safe list fallback, never claim restoration succeeded.
- Dispose location handlers and JS modules with the component. Do not expand restoration to unrelated lists in this slice.

## 6. Implementation tasks

Each task is a coherent, test-first change. Tasks 1 and 2 establish contracts before the UI move; Tasks 3 and 4 can be developed independently after Task 1. The table move in Task 5 depends on Tasks 1, 2 and 4. Return wiring depends on Tasks 3 and 5. Task 7 depends on the integrated UI.

### Task 1: Prove sort and define shared state/filtering

**Existing files:** `Components/Pages/Dashboard.razor`, `Reporting/DashboardSummary.cs`, `Reporting/DashboardGrouping.cs`, `Reporting/DashboardGroupChoice.cs`, corresponding Reporting tests.

**New files:** small `Reporting/DomainListFilters.cs` and `DomainListState.cs` helpers, with matching tests. Keep query parsing alongside this state rather than introducing a generic filtering framework.

- [ ] Spike and record the working installed MudTable sorting/restoration approach in this plan. Remove disposable spike code.
- [ ] Extract filter semantics, query validation and shared sort keys with deterministic domain-ID tie-breaking. Inventory state includes management criteria; Dashboard uses the applicable subset.
- [ ] Test filter composition, identical membership across view modes, empty-group removal, hidden memberships, multi-group duplicates versus distinct totals, target monitored semantics, text/null cases and pass-rate bounds.
- [ ] Preserve the current All my groups contract and test scope narrowing after a claim change.

### Task 2: Make defaults backward-compatible

**Files:** `Reporting/DashboardPreferences.cs`, `Reporting/DomainListState.cs`, `test/DotMarc.Tests/Reporting/DashboardPreferencesTests.cs`; add focused codec tests if needed.

- [ ] Test actual legacy JSON fixtures, no saved settings, new-format round trips, target/All my groups/sort preservation, separate per-page save/clear and no persisted free text.
- [ ] Implement the versioned envelope with the existing table and identity helper. Preserve existing data until a successful explicit write.
- [ ] Test malformed/future settings, deleted tags/groups, reduced access and read/write failure notification/logging.

### Task 3: Implement list-return memory

**Files:** new scoped `Reporting/ListPositionMemory.cs`, `wwwroot/listPosition.js`, associated tests and DI registration in `Program.cs`. Follow existing JS interop and asset-loading patterns.

- [ ] Test entry/user isolation, save/restore, exact expiry boundary, invalid versions, corruption and safe local destinations.
- [ ] Implement history markers and storage with explicit navigation reason; do not infer fresh versus Back from timestamp alone.
- [ ] Cover unavailable storage and disposal; defer page wiring until Task 6.

### Task 4: Move tag management

**Files:** `Components/Pages/ManageGroups.razor`, new `Components/Pages/ManageTags.razor`, `Components/Layout/MainLayout.razor`, security policy registration and component/service tests.

- [ ] Move existing tag CRUD/filter UI and handlers, retaining `TagManagementService`, allowed colours, assignment links, counts, validation and audit behaviour.
- [ ] Adjust page and navigation policy composition so a tag-only administrator can reach Tags and group-only administrators can reach Groups. Individual operation policies remain authoritative.
- [ ] Replace removed tag content with a discoverable Tags link for permitted users, including compatibility for any existing Groups-page tag anchor found during implementation.
- [ ] Do not alter group targets, group PSA mapping, client-report controls or group-management filters.

### Task 5: Establish Inventory and one filter panel

**Files:** rename `Components/Pages/ManageDomains.razor` to `DomainInventory.razor`; modify `Dashboard.razor`, `MainLayout.razor`; new `Components/Shared/DomainListFilters.razor`; reuse/extend Reporting read models and existing management services. Update component tests affected by the rename.

- [ ] Merge monitoring and management projections without N+1 queries or moving report bodies into new per-row loads. Preserve split queries where required.
- [ ] Render one filter disclosure outside grouped tables. Move every applicable Group/Tag/header filter into it, with visible active summary and clear semantics.
- [ ] Apply authorisation, shared filtering and sorting before grouping. Show distinct matching-domain totals and a helpful no-match state with a route back to Filters.
- [ ] Preserve management operations and re-check policy/scope in every exposed handler after lowering page admission to `DomainsView`.
- [ ] Add primary Inventory navigation and remove the duplicate management menu destination. Remove the full table from Dashboard in the same usable transition; keep summaries and operational context.
- [ ] Wire the shared single filter panel into retained Dashboard domain content. Do not leave legacy Group/Tag selectors above it. Honour existing global-data labels and permissions.
- [ ] Keep percentages such as `100.0%` unbroken at supported widths, including Inventory cells and retained summary cards; add a browser regression for this roadmap fix.
- [ ] Expose stable distinct filtered domain IDs and ordinary native domain links for slice B. Do not create bulk batch UI yet.

### Task 6: Wire deep links and restore navigation

**Files:** `DomainInventory.razor`, `Dashboard.razor`, `DomainDetail.razor`, `BaselinesDomain.razor`, `Reporting/ListPositionMemory.cs`, related component and Reporting tests.

- [ ] Change target drill-through URLs and preserve old Dashboard target links without a redirect loop.
- [ ] Handle query changes on an existing component, not only first initialisation; guard superseded data loads.
- [ ] Apply defaults/query/return precedence, render restored sorting and then restore scroll.
- [ ] Implement page Back with trusted-origin checking and Inventory fallback; preserve ordinary new-tab links.
- [ ] Update other actual full-list links to Inventory while retaining genuine action-centre destinations. Include Home, parse failures, scoped-denial wording, docs links and DNS-push fallback/result handling in the link audit; do not mechanically replace every `/dashboard` occurrence.

### Task 7: Integrated validation and documentation

**Tests:** extend Reporting preferences/grouping tests and existing component tests; add `DomainInventoryTests`, filter component tests and focused restoration tests following the repository's existing patterns.

- [ ] Run one targeted .NET invocation combining the affected Reporting, component, management, permissions and API scope tests. Add no new test framework. Warnings must remain errors.
- [ ] Validate browser behaviour against the demo using the existing Edge automation setup, at desktop and narrow/mobile widths. Stop only the app/container processes started for this check.
- [ ] Update `website/docs/getting-started.mdx`, `website/docs/import-domains.mdx`, permission/navigation FAQs and relevant help links to match Inventory and Tags. Update release notes through the existing release-note convention.
- [ ] Reconcile slice B Tasks 7 and 8 with the implemented route, row projection, filtered-ID set and single toolbar. Remove any now-obsolete assumption about Inventory being undecided.
- [ ] Run the existing website documentation build only when website docs are changed. Regenerate/test OpenAPI only if implementation actually changes the API surface.
- [ ] Record completed checks and remaining action-centre/slice B work in the relevant milestone issues. Do not mark the entire Dashboard or bulk-push feature release-ready merely because this prerequisite landed.

## 7. Required acceptance checks

| Scenario | Required result |
| --- | --- |
| All domains, Filters collapsed | Exactly one Filters button, active summary visible, no loose Group/Tag/search controls |
| By group with at least three groups | Exactly one filter panel and one copy of each filter field; no per-table filter buttons/rows |
| Set Tag and minimum pass rate in By group | Every section updates together; empty groups disappear; matches equal All domains' distinct IDs |
| Change view mode or sort | Criteria persist; sorting is consistent across group sections and restores on return |
| Collapse Filters with active criteria | Results remain filtered, `Filters (N)` and readable summary remain visible |
| One domain in two groups | It can appear twice by group, but matching total and future selection count it once |
| Scoped Viewer clears filters or tampers with URL | No additional domains/group names and no write access; locked scope remains evident |
| Multi-group Viewer chooses All my groups | All and only authorised groups participate; saved/restored choice is not mistaken for first group |
| Baselines below/checking/meets link | Exact scoped monitored count/list agreement despite unrelated saved defaults |
| Legacy Dashboard target link | Reaches canonical Inventory once; normal Dashboard and DNS-push error feedback still work |
| Existing saved defaults | Inventory retains view and every legacy criterion; Dashboard retains applicable criteria; saves/clears are independent |
| Open domain at 500px then page Back/browser Back | Original filters, view, sort and near-identical scroll restore after rows render |
| Fresh menu visit/new-tab detail/direct alert link | No stale-state resurrection; safe Inventory fallback, no off-site history navigation |
| Removed domain, access loss, expired state | Revalidate rows/scope, clamp scroll, explain discarded state rather than showing unauthorised/stale data |
| Reorder, delete, group/tag assignment, PSA override | Existing services, audit and policies still hold; Viewer/stale unauthorised actions are refused |
| Tags-only administrator | Can reach permitted Tags operations without access to group-management operations |
| Small screen and keyboard | Panel controls remain usable, focus/expanded state correct, percentages do not wrap |

## 8. Implementation notes and validation

- `/domains` now renders `DomainInventory`, backed by `DomainWorkspace`, which owns the common read/filter/default logic. Dashboard reuses that logic without rendering the Inventory table.
- `DomainListState` owns filters, allowlisted query parsing and externally controlled sorting. The installed MudTable is used only to present pre-sorted rows: native accessible header buttons update one page state rather than restoring private sort state separately in every group table. Browser checks exercise shared sort across groups and restore it on Back.
- `DomainListFilters` is rendered once per page, outside every table and group loop. All existing group, tag, health and management criteria are inside its disclosure; collapsed summaries retain selected names. Grouping runs after filtering.
- The existing `UserDashboardPreferences` JSON row holds a versioned two-page envelope. Legacy JSON remains readable and is upgraded only on explicit save/clear. Unsupported or corrupt settings are reported and not overwritten.
- Return memory is a small JS module, `wwwroot/listPosition.js`, used directly through JS interop. A separate scoped .NET memory service and DI registration proved unnecessary: the existing typed list state is the interop contract. Storage/history tests run with Node, while Edge exercises the real Blazor lifecycle. Origin markers survive domain-tab changes, expire at 30 minutes and are isolated by user/history entry; sign-out clears them.
- Tag CRUD moved to `/tags`, retaining existing services, colours, audit and confirmation behaviour. Groups and Tags navigation now has separate composed management policies.
- Inventory handlers re-check operation policies and domain scope; group assignment retains hidden memberships and requires at least one authorised group for scoped editors. Scoped creation requires an explicit group and the corresponding assignment permission.
- The browser check found and fixed zero-width management pickers when health and management columns were shown together; management controls now retain usable minimum widths in the horizontally scrollable table.
- Validation: 638 tests passed across affected Reporting, Components, management, permissions and API-write suites. A final targeted run after validation/navigation refinements passed 48 tests, including rejection of Inventory-only management criteria on Dashboard rather than applying hidden filters. Four Node tests cover matching entries, fresh visits, user/route isolation, exact expiry, invalid storage and safe Back fallback. The website documentation build passed.
- `website/scripts/test-inventory-browser.mjs` runs against an isolated demo using installed Edge. It checks one panel in both modes, group/tag field uniqueness, filter-before-grouping, shared sort, tag assignment/filtering, per-page saved defaults, collapsed active filters, page/browser Back and scroll, fresh visits, target links, narrow layout, percentage wrapping and scoped Viewer access.
- The detailed task list above records the intended implementation contract; the notes here name the actual files and the simpler memory/sort choices used. No new API surface or database schema was introduced.

## 9. Handoff and completion boundary

This plan is complete when Inventory replaces both full domain lists, all domain filters are in one page-level disclosure in both display modes, management is preserved, saved choices migrate and Back restores position. The next steps are the dedicated action-centre implementation and slice B's review/demo/batch UI, coordinated against the same filtered domain identity model.

Real-provider validation, full action-centre acceptance and the remaining correctness features are still 0.14 release gates. This plan does not reduce their scope.
