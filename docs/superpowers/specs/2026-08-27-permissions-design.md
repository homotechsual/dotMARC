# Permissions Design

## Overview

dotMARC currently has exactly one authorization rule: `RequireAuthenticatedUser()`.
The dashboard's Entra app registration is multi-tenant
(`AzureADMultipleOrgs`), which means every signed-in user from *any*
organization's Entra ID tenant — not just this MSP's own staff — currently
gets full, undifferentiated access: add/remove/reorder domains, create and
delete groups and tags, reassign any domain to any group. This is a live
gap, not a hypothetical one.

This design adds a permissions layer on top of the existing sign-in flow
(which is left entirely unchanged) so that access is deliberately granted,
per person, at whatever granularity actually matters — and so that granting
access to an internal staff member and an external client's contact looks
and works the same way.

## Goals

- Fine-grained, independently-grantable permissions covering every write
  surface in the app today (Domains, Groups, Tags) plus the permission
  system itself.
- Two built-in role presets — `Admin` (everything, locked) and `Viewer`
  (view-only, the only role that can be restricted to specific Groups) —
  plus the ability to define custom roles bundling any subset of
  permissions.
- Granting access is identical regardless of whether the person is internal
  staff or an external client contact: type an email, pick a role, pick a
  Group scope if the role is Viewer.
- Access can be granted by email before someone's first sign-in (a pending
  grant), and binds to their stable Entra object ID the first time they
  actually authenticate, so a later UPN/email rename can't silently break
  their access.
- A deterministic, race-free way to establish the very first Admin, that
  works identically for a fresh deployment and for this app's existing live
  deployment picking up the feature for the first time.
- Sign-in itself is completely unchanged — still multi-tenant Entra ID, no
  new identity provider, no guest invitations.

## Non-goals

- **Entra B2B guest invitations or any other external identity
  provisioning.** Multi-tenant sign-in already covers any client whose own
  organization has an Entra ID tenant — which, given DMARC-caring
  organizations skew IT-mature, is expected to cover the realistic
  population. B2B's only marginal value is a client with zero Entra
  presence at all; that's a narrow edge case not worth the added Graph
  permission, admin consent, and guest-redemption UX friction right now. If
  it's ever actually needed, it's a separable follow-up, not a dependency
  of this design.
- **Scoping anything other than the `Viewer` role to a Group.** A person
  with any edit/delete/reorder/access-management permission has it
  globally, never scoped to specific Groups. Combining fine-grained
  permissions with per-Group scoping on every role is a real feature some
  enterprise IAM systems have, but it roughly doubles this design's surface
  for a case that hasn't come up.
- **A general-purpose role/policy builder.** Roles are a fixed enum of
  permissions with checkboxes, not an expression language.
- **Audit history of who granted or revoked what, and when.** Worth having
  eventually for a security-sensitive feature like this, but wasn't part of
  what was asked for here — a natural fast-follow once the core model
  exists, not part of this design.

## Data model

Three new pieces:

**`Permission`** — a fixed, code-defined enum (matching this project's
existing convention for closed sets, e.g. `DmarcCheckStatus`), one member
per capability the app's UI actually has today:

```csharp
DomainsView, DomainsAdd, DomainsEdit, DomainsReorder, DomainsDelete,
GroupsView, GroupsAdd, GroupsRename, GroupsDelete,
TagsView, TagsAdd, TagsEdit, TagsDelete,
AccessManage
```

`DomainsEdit` covers the monitored toggle and group/tag assignment;
`AccessManage` is deliberately its own permission, separate from
everything else, since it's the one permission that lets someone change
who else has access — including their own.

**`Role`** — `Id, Name, IsLocked, List<Permission> Permissions`. Two rows
are seeded at migration time: `Admin` (`IsLocked = true`, every
`Permission`) and `Viewer` (`IsLocked = false`, `DomainsView`, `GroupsView`,
`TagsView`). `IsLocked` is enforced in the service layer, not just hidden
in the UI — `Admin`'s permission set and name can never be changed or
deleted through any code path, so it stays a reliable break-glass account
regardless of what happens to any other role. Anyone with `AccessManage`
can create, rename, edit the permission set of, or delete any non-locked
role.

**`UserAccess`** — one row per granted person: `Id, Email,
EntraObjectId (nullable), RoleId, List<Group> ScopedGroups`.
`ScopedGroups` is many-to-many with `Group` (same shape as
`Domain.Groups`), and is only ever meaningful when `RoleId` points at the
`Viewer` role — the service layer ignores/clears it for any other role,
the same "don't trust the UI alone" pattern already used for the tag color
palette. An empty `ScopedGroups` list on a Viewer grant means unrestricted
view access (an internal read-only user, e.g. a junior support tech or an
auditor) — a non-empty list restricts them to just those Groups' domains
(the external-client case). This mirrors the Dashboard's own Group filter,
where no filter selected already means "show everything."

`Email` is what an admin actually types and stays authoritative for a
grant that hasn't been claimed yet. `EntraObjectId` starts `null` and gets
filled in the first time that email successfully signs in — from then on,
lookups use the object ID, so a later email/UPN change on the Entra side
can't orphan someone's access. A grant made against an email that later
signs in under a *different* email (unlikely, but possible if someone's
UPN changes before their first login) simply never binds — the person
would need re-inviting under the new address, which is an acceptable,
rare-edge-case cost for the stability this buys everywhere else.

## Authorization enforcement

Enrichment happens once per sign-in, via `IClaimsTransformation` — after
Entra authentication succeeds, look up the caller's `UserAccess` row (by
`EntraObjectId` if bound, falling back to matching and binding by `Email`
on first login), and add a claim per granted `Permission` plus a claim
listing accessible Group IDs (empty = unrestricted) to the
`ClaimsPrincipal`. This is the standard ASP.NET Core pattern for layering
app-specific authorization onto an already-authenticated identity, and it
plays directly into `[Authorize(Policy = "...")]` and `AuthorizeView`
without needing a database round-trip on every single check.

The global fallback policy tightens from `RequireAuthenticatedUser()` to a
custom policy requiring "authenticated AND has at least one `UserAccess`
row" — an authenticated-but-ungranted user (which, as of today, is anyone
from any Entra tenant) lands on the existing `AccessDenied.razor` instead
of the app. Each `Permission` gets its own registered policy
(`DomainsAdd`, `GroupsDelete`, etc.), used two ways:

- **Page-level**: Dashboard and Domain Detail both require `DomainsView`
  (which every built-in and sensibly-defined custom role is expected to
  carry — this is the one permission a grant needs to see anything at
  all). Manage Domains requires at least one of
  `DomainsAdd`/`DomainsEdit`/`DomainsReorder`/`DomainsDelete`; Manage
  Groups requires at least one of the Groups/Tags write permissions; Manage
  Access requires `AccessManage`. A pure `Viewer` never reaches any of the
  configuration pages — the pages themselves redirect, not just individual
  buttons.
- **Action-level**: individual buttons/controls are wrapped in
  `AuthorizeView` for their specific permission and are *hidden*, not just
  disabled, when the signed-in user doesn't have it — matching the
  "Manage Domains is pure configuration" philosophy already established
  for that page: someone who can't edit shouldn't be shown edit controls
  at all.

**Scope enforcement** on the two view surfaces:

- **Dashboard**: if the signed-in user's Group scope is non-empty, the
  existing Group filter dropdown (from the domain-grouping feature) is
  locked to that set — no "all domains" option, no selecting a Group
  they're not scoped to.
- **Domain Detail**: since this page is reached by a direct URL
  (`/domains/{name}`), it independently checks that the requested domain
  belongs to one of the caller's scoped Groups before rendering — a scoped
  Viewer can't work around the Dashboard's filtering by navigating
  straight to an out-of-scope domain's URL.

## New page: Manage Access

Following the Manage Groups precedent: one page, gated entirely behind
`AccessManage`, two sections:

- **Roles**: lists `Admin` (shown, but its fields are read-only) alongside
  `Viewer` and any custom roles (name + permission checkboxes, both
  editable), with add/rename/delete for anything not locked.
- **Access grants**: lists every `UserAccess` row — email, whether it's
  bound yet (has an `EntraObjectId`) or still pending, role, and scoped
  Groups if the role is `Viewer` — with an add-by-email form (email, role
  picker, Group picker shown only when `Viewer` is selected) and a revoke
  button per row.

## Bootstrap

A new `InitialAdmins__Emails` environment variable (a delimited list of
emails), following the same configuration convention as this project's
existing `Graph__*`/`EntraId__*` settings. On startup, if the `UserAccess`
table is completely empty, one `UserAccess` row is created per listed
email, granted the `Admin` role, unscoped. If the table already has any
rows, this is a no-op — the env var only matters for that first bootstrap
moment, and every subsequent access change goes through the Manage Access
page.

This same "empty table" condition is what makes this work identically for
a fresh deployment and for this app's *existing* live deployment: from the
migration's perspective there's no difference between the two — either
way, `UserAccess` starts empty, since there's no way to retroactively know
who's been using the app so far. The operator sets `InitialAdmins__Emails`
to their own email (and any other current staff who should start as
Admin) before this feature's deployment goes out.

The migration that creates these tables, the seed-on-empty-table logic,
and the tightened fallback policy all ship in the same release — the seed
check runs at application startup, before any request is served, so there
is no window where enforcement is live but seeding hasn't happened yet.

## Testing

Following this project's established `Testcontainers.PostgreSql` pattern:

- `Role`/`UserAccess` CRUD service tests: creating custom roles, rejecting
  edits to `Admin` (name, permissions, or delete), case-insensitive role
  name uniqueness, granting/revoking access, setting/clearing scoped
  Groups (and confirming a non-Viewer role's scope is ignored/cleared).
- Bootstrap: empty table + env var → grants created for each listed email;
  non-empty table + env var → no-op, existing rows untouched.
- Email-to-object-ID binding: a pending grant (no `EntraObjectId` yet)
  resolves and binds on a simulated first sign-in; a second sign-in from
  the same person is looked up by object ID directly.
- Permission-check logic: a user with a given permission set passes the
  matching policy checks and fails the rest.
- Scope enforcement: a Viewer scoped to Group A can see Group A's domains
  and cannot see Group B's, including via direct Domain Detail navigation;
  an unscoped Viewer sees everything.
- No automated test for the Blazor UI itself, consistent with this
  project's established precedent (no component-rendering test framework)
  — manual verification steps instead, same as every prior UI-only task
  this session.
