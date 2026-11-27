# Tabbed settings pages: Alerts & PSA, and Logs

Status: short design, 24 November 2026. Added to 0.14.0 by the user. Issues #76 and #77. Same pattern as the Baselines page in the [DMARC target policy design](2026-11-24-dmarc-target-policy-design.md).

## Intent

The Manage menu has grown one entry per settings screen. The user wants related screens grouped onto one page each, with a tab and sub-route per screen, so they are easier to find and the menu is shorter.

## Pages

**Alerts & PSA** (one menu entry, replacing "Alert rules", "Alert settings" and "PSA settings"):

* `/alerts/rules`: Alert rules (route unchanged).
* `/alerts/settings`: Alert settings (route unchanged).
* `/alerts/psa`: PSA settings, moved from `/psa/settings`, which redirects here.

`/alerts` stays the alert inbox in the top bar and is not a tab.

**Logs** (one menu entry, replacing "Server logs" and "Audit log"):

* `/logs/server`: Server logs, moved from `/logs`.
* `/logs/audit`: Audit log, moved from `/audit`, which redirects here.
* `/logs` opens the first tab the person can use.

## Behaviour

* Each tab keeps its current page, behaviour and permission (`AlertRuleAccess.MayView`, `AlertsManage`, `LogsView`, `AuditView`). Tabs a person cannot use are hidden. The menu entry shows when at least one tab is usable.
* Each tab is a real route, so links, bookmarks, Back and refresh land on the right tab.
* Old routes redirect, keeping any query string (the audit log's filters, for example).
* In-app links (including the docs link on Alert rules) and docs pages that name the old routes or menu entries are updated.

## Approaches

1. **A shared tab shell component plus the existing pages as tab bodies (chosen, my call).** One small layout component renders the title, tabs (filtered by permission) and the active page. Baselines, Alerts & PSA and Logs all use it. The existing pages move largely as they are.
2. **One page per group holding every screen's markup.** Fewer files, but merges unrelated code and makes each page harder to change.
3. **Menu sub-headings only, no tabs.** Cheapest, but not what was asked.

## Constraints and notes

* Routes are kept where they already sit under `/alerts`, to avoid needless redirects.
* 0.15's ticketing work (#55) reshapes the PSA and alert settings screens; the tab shell should not get in its way.
* Group-scoped pages (`/groups/{id}/senders`, group alert/ticket panels) are unchanged.

## Acceptance criteria

* Each old route redirects to its tab, keeping the query string.
* A person sees only the tabs they may use, and a person with none sees no menu entry.
* Refresh and Back keep the selected tab.
* The menu shows Baselines, Alerts & PSA and Logs in place of the six entries they replace.
