# Alerts & PSA and Logs pages Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Alert rules, alert settings and PSA settings become tabs of one "Alerts & PSA" page, and server logs and the audit log become tabs of one "Logs" page, each tab its own route, using the tab shell built for Baselines.

**Architecture:** Each tab set gets a small static class like `BaselineTabs` (`For` takes policy results, `ForAsync` makes the checks), so the pages and the menu share one list. Existing pages keep their logic and wrap their content in `TabbedPage`; their own `h4` headings go, because the shell shows the page title. Old routes go through `LegacyRedirectTargets`, which keeps the query string.

**Tech Stack:** .NET 10, Blazor (MudBlazor), xUnit, Docusaurus docs.

**Spec:** [Tabbed settings pages](../specs/2026-11-24-tabbed-settings-pages-design.md) (issues #76 and #77, roadmap items 21 and 22).

## Global Constraints

* Routes: `/alerts/rules` and `/alerts/settings` unchanged; PSA settings moves from `/psa/settings` to `/alerts/psa`. Server logs moves from `/logs` to `/logs/server`; audit log moves from `/audit` to `/logs/audit`; `/logs` opens the first tab the person can use.
* `/alerts` stays the alert inbox and is not a tab.
* Each tab keeps its current page, behaviour and permission: Alert rules `AlertsView` policy plus `AlertRuleAccess.MayView`; Alert settings and PSA `AlertsManage`; Server logs `LogsView`; Audit log `AuditView`. Tabs a person cannot use are hidden; a menu entry shows only when at least one tab is usable.
* Old routes redirect keeping the query string (the audit log's filters, the PSA OAuth result parameters).
* Menu: "Alerts & PSA" replaces "Alert rules", "Alert settings" and "PSA settings"; "Logs" replaces "Server logs" and "Audit log".
* No em dashes in any prose, comment, UI copy, docs or commit message. Meaningful variable names. Warnings fail the build. No hard line wraps in markdown.
* There is no component test host: test the pure tab classes and the redirect map, check routes and `[Authorize]` attributes by reflection, and check rendering in a browser with the playwright-edge tools (Postgres on 55432, since 5432 is blocked on this machine).
* Branch `develop`, one commit per task, no push without the user's go-ahead.

## Review Focus

* `/audit?action=domain.updated&page=2` (or whatever filters the audit page reads) lands on `/logs/audit` with the same filters applied (Task 2).
* The PSA OAuth callbacks in `Program.cs` that redirect to `/psa/settings?psa=...` land on `/alerts/psa` with their result message shown (Task 1).
* A person with `AlertsManage` but not `AlertsView`, or `AlertsView` but limited to groups (so `AlertRuleAccess.MayView` is false), sees only the tabs they can use, and the menu entry goes to the first of them (Task 1).
* A person with `AuditView` only goes from the menu and from `/logs` straight to `/logs/audit` (Task 2).
* Refreshing on a tab, or pressing Back after switching tabs, keeps the right tab selected (Tasks 1 and 2, browser check).

---

### Task 1: Alerts & PSA page

**Files:**
- Create: `src/DotMarc/Notifications/AlertsPsaTabs.cs`
- Modify: `src/DotMarc/Components/Shared/TabbedPageNavigation.cs` (add `FirstVisible`), `src/DotMarc/Baselines/BaselineTabs.cs` (its `FirstVisible` delegates to the shared one), `src/DotMarc/Components/Pages/AlertRules.razor`, `src/DotMarc/Components/Pages/AlertsSettings.razor` (also its link to PSA settings), `src/DotMarc/Components/Pages/PsaSettings.razor` (route `/alerts/psa`), `src/DotMarc/Components/Pages/LegacyRedirects.razor` and `LegacyRedirectTargets.cs` (add `/psa/settings`), `src/DotMarc/Components/Layout/MainLayout.razor`, `src/DotMarc/Program.cs` (the NinjaOne callback redirects, and any other `/psa/settings` redirect)
- Test: `test/DotMarc.Tests/Components/AlertsPsaPagesTests.cs`, existing `AlertRulesPageTests.cs` (update only if it asserts a heading or route that changes), existing redirect tests in `BaselinesPagesTests.cs`

**Interfaces:**
- Produces:
  * `static TabbedPageTab? TabbedPageNavigation.FirstVisible(IReadOnlyList<TabbedPageTab> tabs)`.
  * `static class AlertsPsaTabs` with `const string RulesHref = "/alerts/rules"`, `SettingsHref = "/alerts/settings"`, `PsaHref = "/alerts/psa"`; `IReadOnlyList<TabbedPageTab> For(ClaimsPrincipal user, bool canViewAlerts, bool canManageAlerts)` giving tabs "Rules" (visible when `canViewAlerts && AlertRuleAccess.MayView(user)`), "Settings" and "PSA" (visible when `canManageAlerts`), in that order; `Task<IReadOnlyList<TabbedPageTab>> ForAsync(ClaimsPrincipal user, IAuthorizationService authorizationService)` checking `nameof(Permission.AlertsView)` and `nameof(Permission.AlertsManage)`.
  * Page title "Alerts & PSA" on all three tabs.

- [ ] **Step 1: Write failing tests:** `AlertsPsaTabs.For` gives all three tabs for unlimited staff with both permissions; only Settings and PSA with `AlertsManage` alone; only Rules for `AlertsView` alone; no Rules tab for a group-limited person with `AlertsView` (claims built as in `SenderDecisionAccessTests`); `ForAsync` with a stub authorization service granting by policy name (reuse `PolicyNamedAuthorizationService` in `test/DotMarc.Tests/Internal`); `TabbedPageNavigation.FirstVisible` returns the first visible tab or null; `PsaSettings` routes to `/alerts/psa` and not `/psa/settings`, keeping `[Authorize(Policy = "AlertsManage")]` (use `nameof(Permission.AlertsManage)` if the page already does); `LegacyRedirectTargets.DestinationFor(new Uri("https://x/psa/settings?psa=ninjaone&ninjaone=connected"))` is `/alerts/psa?psa=ninjaone&ninjaone=connected`; `LegacyRedirects` carries `@page "/psa/settings"`.
- [ ] **Step 2: Run** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DotMarc.Tests.Components"`. Expected: FAIL.
- [ ] **Step 3: Implement.** Wrap each page's content in `<TabbedPage Title="Alerts & PSA" Tabs="_tabs">`, loading `_tabs` with `AlertsPsaTabs.ForAsync`, and remove the page's own `h4`; keep any explanatory text under it. Replace the three menu entries with one "Alerts & PSA" entry whose `Href` is the first visible tab, computed with `ForAsync` and `FirstVisible` and refreshed on `AuthenticationStateChanged` as the Baselines entry is. Point `Program.cs` redirects and the in-app link straight at `/alerts/psa`.
- [ ] **Step 4: Run** the same filter and `dotnet build`. Expected: PASS, zero warnings.
- [ ] **Step 5: Browser check** (demo mode, admin persona): the menu shows "Alerts & PSA"; each tab opens and works as before; `/psa/settings?psa=ninjaone&ninjaone=not-configured` lands on `/alerts/psa` with the same message the page showed before; refresh and Back keep the tab; `/alerts` is still the inbox. Stop the app afterwards.
- [ ] **Step 6: Commit** `Combine alert rules, alert settings and PSA settings into one Alerts & PSA page`.

### Task 2: Logs page

**Files:**
- Create: `src/DotMarc/Components/Pages/LogsTabs.cs` (or beside the server logs code, following where `ServerLogs` lives), `src/DotMarc/Components/Pages/LogsIndex.razor`
- Modify: `src/DotMarc/Components/Pages/ServerLogs.razor` (route `/logs/server`), `src/DotMarc/Components/Pages/AuditLogPage.razor` (route `/logs/audit`), `LegacyRedirects.razor` and `LegacyRedirectTargets.cs` (add `/audit`), `src/DotMarc/Components/Layout/MainLayout.razor`, any in-app link to `/audit` or `/logs` (search `src`)
- Test: `test/DotMarc.Tests/Components/LogsPagesTests.cs`

**Interfaces:**
- Consumes: `TabbedPageNavigation.FirstVisible` (Task 1).
- Produces: `static class LogsTabs` with `const string ServerHref = "/logs/server"`, `AuditHref = "/logs/audit"`; `For(ClaimsPrincipal user, bool canViewServerLogs, bool canViewAudit)` giving "Server" then "Audit" tabs; `ForAsync(user, authorizationService)` checking `nameof(Permission.LogsView)` and `nameof(Permission.AuditView)`. Page title "Logs". `LogsIndex` at `/logs` redirects (replace) to the first visible tab, or shows "You do not have access to any logs." when none, as `BaselinesIndex` does.

`/logs` is now the index rather than server logs, so it needs no redirect entry; only `/audit` does.

- [ ] **Step 1: Write failing tests:** `LogsTabs.For` and `ForAsync` for each permission combination; `ServerLogs` routes to `/logs/server` with `LogsView`, `AuditLogPage` to `/logs/audit` with `AuditView`; `LogsIndex` is at `/logs`; `DestinationFor(new Uri("https://x/audit?action=domain.updated"))` is `/logs/audit?action=domain.updated`; `LegacyRedirects` carries `@page "/audit"`.
- [ ] **Step 2: Run** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DotMarc.Tests.Components|FullyQualifiedName~DotMarc.Tests.ServerLogs|FullyQualifiedName~DotMarc.Tests.Audit"`. Expected: FAIL.
- [ ] **Step 3: Implement**, wrapping both pages in `<TabbedPage Title="Logs" Tabs="_tabs">` and removing their own `h4`. Replace the two menu entries with one "Logs" entry pointing at `/logs`, shown when either tab is visible. If the audit page builds links to itself (paging, filters) using `/audit`, change them to `/logs/audit`. Leave the API route `/api/.../audit` alone.
- [ ] **Step 4: Run** the same filter and `dotnet build`. Expected: PASS, zero warnings.
- [ ] **Step 5: Browser check:** the menu shows "Logs"; `/logs` opens Server; both tabs work as before; `/audit` with a filter in its query lands on `/logs/audit` with the filter applied; paging on the audit tab stays on `/logs/audit`; refresh and Back keep the tab. Stop the app afterwards.
- [ ] **Step 6: Commit** `Combine server logs and the audit log into one Logs page`.

### Task 3: Docs and release notes

**Files:**
- Modify: every page under `website/docs` that names the old menu entries or routes (at least `alert-rules.mdx`, `alerts.mdx`, `audit-log.mdx`, `getting-started.mdx`, `import-domains.mdx`, `permissions-and-access.mdx`, `server-logs.mdx`, `psa-integration.mdx` and `psa/*.mdx`; find them all with a search for "Alert rules", "Alert settings", "PSA settings", "Server logs", "Audit log", "/psa/settings", "/audit" and "/logs"), the roadmap release notes draft (`docs/superpowers/plans/2026-11-20-v0-14-0-roadmap.md`)
- Test: `test/DotMarc.Tests/Components/DocsLinkTests.cs` and `TextLinksTests.cs` (run; update only if they pin old text)

- [ ] **Step 1: Update the docs** so menu paths read **Manage > Alerts & PSA > Rules** (or Settings, PSA) and **Manage > Logs > Server** (or Audit), and any literal route uses the new one. Do not change the API docs' `/api` audit endpoint. Release notes: Alerts & PSA and Logs pages with tabs; old addresses (`/psa/settings`, `/audit`) redirect; `/logs` now opens the first logs tab you can use.
- [ ] **Step 2: Run** `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DotMarc.Tests.Components"`. Expected: PASS.
- [ ] **Step 3: Run the full suite** `dotnet test test/DotMarc.Tests --blame-hang --blame-hang-timeout 10m`. Expected: all pass.
- [ ] **Step 4: Commit** `Point the docs at the Alerts & PSA and Logs pages`.
