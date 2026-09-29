# Audit logging: who changed what, who signed in, and what they looked at

Date: 2026-09-29. Status: approved design, not yet planned or built. Target release: v0.8.0.

## Problem

dotMARC lets other people, including client contacts, see and change things: access grants, roles, alert and
PSA settings, DNS provider credentials, and DNS records pushed to live zones. Nothing records who did any of
it. Once more than one person has access, "who changed this?" has no answer, and security questionnaires
expect one. This is the "Audit logging for all changes / configuration alterations and access" idea on the
roadmap.

This spec covers the audit log only. It comes first in 0.8.0 because the other 0.8.0 features (bulk import,
SPF and DKIM push, the public API) are all actions that should be recorded from the day they ship.

## Decisions

1. **Three kinds of entry:** changes, sign-ins and page views. "Access" in the idea's title covers both
   signing in (including being refused) and which pages someone opened.
2. **Changes are recorded by the service that makes them**, not by an Entity Framework save hook and not by
   the pages. Each mutating service method takes an `AuditActor` and adds its entry to the same database
   save as the change, so the change and its record succeed or fail together. The future public API passes
   its key as the actor, which recording from the pages could not cover.
3. **Entries are readable, not raw diffs:** a summary such as "Renamed group Client A to Client B", plus a
   list of changed fields with old and new values.
4. **Secrets are never stored.** A changed secret is recorded as changed, with no values.
5. **Only what actually changed is recorded.** Refused or failed actions (a duplicate name, the last-admin
   guard, a validation error) record nothing.
6. **Retention is set per kind**, as a number of days or "keep forever". Defaults: changes 365 days,
   sign-ins 365 days, page views 90 days.
7. **Two new permissions:** `AuditView` to read and export the log, and `AuditManage` to change retention.
   They are separate so an auditor can read the log without being able to shorten it.
8. **The log can't be edited.** Nothing in the UI edits or deletes an entry. Entries leave only by retention.

## Data model

### `AuditEntry`

| Column | Type | Notes |
| --- | --- | --- |
| `Id` | `long` | Identity. |
| `OccurredUtc` | `DateTimeOffset` | When it happened. |
| `Kind` | `AuditEntryKind` | `Change`, `SignIn` or `PageView`, stored as a string. |
| `ActorKind` | `AuditActorKind` | `User` or `System`, stored as a string. `ApiKey` is added with the public API. |
| `ActorObjectId` | `string?` | The Entra object id, for a person. |
| `ActorEmail` | `string?` | The email as it was at the time, so the entry stays readable after the grant is revoked. |
| `ActorName` | `string` | A display name: the person's name or email, or a system actor's name such as "Alert monitor". |
| `Action` | `string` | A stable code, for example `group.renamed` (see the catalogue below). |
| `TargetType` | `string?` | For example `Domain`, `Group`, `Role`, `UserAccess`, `Settings`. |
| `TargetId` | `string?` | The target's id as text, so non-integer ids fit. |
| `TargetName` | `string?` | The target's display name at the time, for example the domain name. |
| `Summary` | `string` | One readable sentence. |
| `Changes` | `jsonb` | A list of `{ "field": ..., "old": ..., "new": ..., "secret": bool }`. Empty for sign-ins and page views. For a secret, `old` and `new` are null and `secret` is true. |

Indexes: `OccurredUtc` descending; (`Kind`, `OccurredUtc`); `ActorEmail`; (`TargetType`, `TargetId`).

### `AuditSettings`

A single row (`Id = 1`, seeded with `HasData` like `NotificationSettings`) holding `ChangeRetentionDays`,
`SignInRetentionDays` and `PageViewRetentionDays`, each an `int?` where null means keep forever. Seeded as
365, 365 and 90. Allowed values are 1 to 3650 days, or null.

### Permissions

`AuditView` and `AuditManage` are appended to `Permission`, with authorization policies registered the same
way as the others. The built-in Admin role is given every `Permission` at startup by `AccessBootstrapper`, so
Admin gets both without a migration. A custom role gets them from Manage access.

## Capturing changes

### `AuditActor`

A record carrying `Kind`, `ObjectId`, `Email` and `Name`, with two constructors:

* `AuditActor.FromPrincipal(ClaimsPrincipal)` for a signed-in person, reading the object id and email with the
  same claim fallbacks as `UserAccessClaimsTransformation`.
* `AuditActor.System(string name)` for work nobody clicked, for example "Startup" for seeding the initial
  admins, or "HaloPSA webhook".

Pages get the current actor from a small scoped service, `AuditActorAccessor`, that reads the
`AuthenticationStateProvider`. Minimal API endpoints use `HttpContext.User`.

### Recording

A static `AuditLog.Record(context, actor, action, target, summary, changes)` adds an `AuditEntry` to the
context without saving, so it is saved by the service's own `SaveChangesAsync`. A small `AuditChanges`
builder compares old and new values and only includes fields that differ, with `AuditChanges.Secret(name)`
for a secret that changed.

### What is recorded

Every mutating public method on these services takes an `AuditActor` (before its `CancellationToken`) and
records one entry:

* `DomainManagementService`: `domain.added`, `domain.removed`, `domain.monitoring_changed`,
  `domain.halo_client_changed`, `domain.mta_sts_changed`, `domain.dkim_selectors_changed`, `domains.reordered`
  (one entry for the whole reorder, not one per row).
* `GroupManagementService`: `group.added` (noting the Halo client when created from Halo), `group.renamed`,
  `group.removed`, `group.halo_client_changed`, `domain.groups_changed` (one entry listing the groups added
  and removed).
* `TagManagementService`: `tag.added`, `tag.updated`, `tag.removed`, `domain.tags_changed`.
* `RoleManagementService`: `role.added`, `role.updated` (name and permissions added or removed), `role.removed`.
* `UserAccessManagementService`: `access.granted`, `access.updated`, `access.revoked`.
* `AlertTicketRuleService`: `ticket_rule.global_changed`, `ticket_rule.group_changed`.
* Settings services: `settings.notifications.saved`, `settings.halo.saved`, `settings.cloudflare_dns.saved`,
  `settings.azure_dns.saved`, `settings.google_cloud_dns.saved`, and `settings.audit.saved` for retention.
  Client secrets and API tokens are recorded as changed, never with values.

Outside the services:

* **DNS pushes**, in the push callback endpoint: `dns.pushed`, one entry per push with each record's name,
  type, old value and new value. A push that doesn't succeed records nothing, matching decision 5; it is
  already logged as a warning.
* **Halo actions** on Alert settings: `halo.integration_tested` and `halo.sign_in_cleared`.
* **Export**: `audit.exported`, with the filters used.

Not recorded as changes: reports and polls being ingested, and alerts being raised or resolved. Those are the
data dotMARC monitors, not administrative actions, and they have their own pages.

Read methods (`Get*`, `List*`, `Count*`, `Resolve*`) are exempt.

## Sign-ins

Recorded where a sign-in actually happens, so it is once per sign-in and not on every request:

* **Entra ID**: in the OpenID Connect `OnTokenValidated` event. `signin.succeeded` when the person has a grant,
  `signin.refused` with the email they tried when they have none.
* **Demo**: in the demo sign-in handler, as `signin.succeeded` for the persona chosen.

Sign-ins are best-effort: if recording fails, a warning is logged and the sign-in continues.

## Page views

`MainLayout` records `page.viewed` on the first render and on each `NavigationManager.LocationChanged`. The
entry holds the path, for example `/domains/contoso.com/sources`, without the query string or fragment, since
DNS push state travels in the query string. The domain in the path is also stored as the target, so views of a
domain can be filtered. Page views are best-effort, like sign-ins.

## The Audit log page

At `/audit`, under **Manage > Audit log**, behind `AuditView`.

* **Table**: newest first, with Time, Kind, Who, Action, Target and Summary. Clicking a row expands its field
  changes as old and new values.
* **Paging and filtering run on the server** (`MudTable` `ServerData`), unlike the other tables, because the
  log is large. Date from and to pickers sit above the table, defaulting to the last 30 days. The table has
  the same Filters toggle and filter row as the other tables: kind, who (search), action (pick from the
  catalogue), target (search) and summary (search).
* **Page views are hidden by default**, behind a **Show page views** switch, so the default view is changes
  and sign-ins.
* **Export CSV** exports exactly what the filters match, streamed from a GET endpoint behind `AuditView` so a
  large export never sits in memory. Field changes are flattened into one column. The export records
  `audit.exported`.
* **Retention panel**, visible with `AuditManage`: a days field or **Keep forever** for each kind, and Save.
* Group scoping doesn't apply: `AuditView` is an administrative permission and shows every entry.

## Retention

`AuditRetentionService`, a `BackgroundService` like the others, runs once a day. For each kind with a retention
period it deletes entries older than that period in batches (`ExecuteDeleteAsync`, 5,000 at a time) so a large
first cleanup doesn't hold one long transaction.

## Demo instances

Demo instances record entries like any other. The daily demo reset clears the audit log along with the rest of
the demo data.

## Errors

* A change's entry is saved with the change. If the save fails, neither is saved and the page shows its usual
  error.
* Sign-in and page view recording failures are logged as warnings and never block the person.
* A failed retention run is logged and tried again the next day.
* CSV export escapes formula injection: a cell starting with `=`, `+`, `-` or `@` gets a leading `'`.

## Testing

* **Each audited service method**: records the expected action, target and field changes, only includes fields
  that changed, and records nothing when it refuses the action.
* **Coverage**: a reflection test that every public static method on the audited services, other than the
  exempt read methods, takes an `AuditActor`, so a new method can't skip recording.
* **Secrets**: saving each settings service with a new secret never puts the secret's value in an entry.
* **Sign-ins**: the token-validated handler records one success for a person with a grant and one refusal for a
  person without.
* **Page views**: the recorded path has no query string or fragment.
* **Retention**: each kind expires on its own period, and a null period keeps everything.
* **Export**: CSV output, including formula escaping and flattened field changes.
* **Browser, on the demo**: filters, expanding a row, the page-view switch, and export.

## Rollout

* One migration adds `AuditEntries` with its indexes, and `AuditSettings` with its seeded row.
* History starts at the upgrade. Nothing from before 0.8.0 can be reconstructed, and the release notes say so.
* Docs: a new Audit log page, and the permissions page gains `AuditView` and `AuditManage`.
* Roadmap: the idea is set to `complete` for `v0.8.0` in `canny-roadmap.json` when it ships.

## Out of scope

* API keys as actors. `AuditActorKind` gains `ApiKey` when the public API is built.
* Recording reads other than page views, such as which reports someone opened.
* Tamper evidence, such as hash-chaining entries, and exporting to a SIEM.
* Recording report ingestion and alert events, which are monitored data, not administrative actions.
