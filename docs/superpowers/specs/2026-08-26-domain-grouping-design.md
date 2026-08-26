# Domain Grouping Design

## Overview

dotMARC currently treats every monitored domain as a flat, unordered-by-owner
list. For an MSP managing many clients' domains, "which client does this
belong to" and "which of my clients need attention" are questions the app
can't answer today — you can reorder domains, but there's no way to say two
domains belong together. This design adds two independent organizing
concepts:

- **Groups** — user-defined, many-to-many containers a domain can belong to
  (typically a client/owner, but a domain can sit in more than one group at
  once — e.g. a client group and a project group).
- **Tags** — a curated, colored label a domain can carry (e.g. "primary"),
  many-to-many, used for filtering rather than ownership.

Both are manually assigned; there is no rule-based/"smart" group whose
membership is computed from tags. That's an explicit non-goal below.

This is the first of two related design cycles. The second — delegated
access permissions scoped to a group — depends on groups actually existing
first, and is deliberately deferred to its own design once this ships. Per
the earlier design-time decision: permissions will attach to Groups only,
never to Tags, so tags stay a pure filtering/labeling concept with no access
implications.

## Goals

- Let a domain be assigned to any number of Groups and any number of Tags.
- Groups and Tags are each a curated list (created/renamed/deleted
  centrally), not free text typed inline per domain.
- Assigning a domain's Groups/Tags is configuration, and lives on Manage
  Domains — consistent with that page's existing "configuration only, no
  status" role.
- The Dashboard gets two independent filter dropdowns (Group, Tag), each
  defaulting to "all domains," narrowing the table to domains that are a
  member of the selected one.
- Tags carry a color (from the same `MudBlazor.Color` palette already
  driving every other status chip in this app), rendered consistently
  wherever a tag chip appears.

## Non-goals

- **Tag-derived "smart groups"** — a group whose membership is computed live
  from a tag rule rather than manually curated. Deferred until real usage
  shows what rules people actually want; building a rules engine ahead of
  that is premature.
- **Permissions / delegated access** — the next design cycle. This design
  only makes Groups exist; it doesn't attach any access control to them.
- **Bulk assignment** — assigning a Group/Tag to many domains at once in a
  single action. Not needed at this app's stated scale (a handful to a few
  dozen domains).
- **Group colors** — only Tags were asked for a color. Groups stay
  uncolored; adding color to Groups later is a small, symmetric follow-up
  if wanted.
- **Cross-tenant group sharing** — out of scope; this is a single-tenant
  app's own organizing concept, not tied to Entra ID groups in any way (that
  linkage, if ever wanted, belongs to the permissions design cycle).

## Data model

Two new entities, following this project's existing plain-POCO entity style
(`Domain`, `ParseFailure`):

```csharp
public sealed class Group
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public List<Domain> Domains { get; set; } = [];
}

public sealed class Tag
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required Color Color { get; set; }
    public List<Domain> Domains { get; set; } = [];
}
```

`Domain` gains two navigation properties:

```csharp
public List<Group> Groups { get; set; } = [];
public List<Tag> Tags { get; set; } = [];
```

Both `Group.Domains` and `Tag.Domains` are EF Core implicit many-to-many
skip navigations — membership carries no extra data (no "added date," no
"role"), so no hand-written join entity is needed; EF generates the join
tables (`DomainGroup`, `DomainTag`) automatically from the two-sided
navigation.

`DotMarcDbContext.OnModelCreating` configures:
- `Group.Name` — case-insensitive unique index, matching `Domain.Name`'s
  existing convention (prevents "Client A" and "client a" both existing).
- `Tag.Name` — same, case-insensitive unique index.
- `Tag.Color` — `HasConversion<string>()`, the same pattern already used for
  `Domain.DmarcCheckStatus` and `ReportRecord`'s `Disposition`/`SpfResult`/
  `DkimResult` — stores the enum's name, not its numeric value, so the
  column stays human-readable and stable across any future enum reordering.

One migration adds both entities, both join tables, and both indexes.

## New page: Manage Groups (`/groups`)

A new nav destination (alongside Dashboard and Manage Domains), following
`ManageDomains.razor`'s existing structural pattern (text field + "Add"
button at top, list below with inline rename and a delete button per row) —
duplicated for two sections on one page rather than two separate pages,
since the workflow (create a group, then go assign it) stays on one screen:

- **Groups section**: name field + Add. List shows each group's name,
  editable inline, with a delete button.
- **Tags section**: name field + a `MudSelect<Color>` color picker + Add.
  List shows each tag as a colored `MudChip`, name editable inline, with a
  delete button. The picker offers `Primary`, `Secondary`, `Tertiary`,
  `Info`, and `Dark` — deliberately excluding `Success`/`Warning`/`Error`,
  which already carry pass/fail/status meaning on the Dashboard's Report
  Status and DNS Status chips; a tag is a neutral organizational label, and
  reusing those three would make a tag chip look like a status indicator.

Deleting a Group or Tag that still has member domains only removes the
membership rows (EF's default many-to-many delete behavior) — it does not
touch the Domain or its reports. A confirm dialog states how many domains
are currently members, mirroring `ConfirmDeleteDomainDialog`'s existing
report-count pattern for domain deletion.

Name validation mirrors `DomainManagementService.AddDomainAsync`'s shape:
non-empty after trimming, case-insensitive uniqueness enforced both by a
pre-check and a unique-index-violation catch (SqlState `23505`) for the
race case, matching that method's existing pattern exactly.

## Manage Domains changes

Two new columns, "Groups" and "Tags," each a `MudSelect` with
`MultiSelection="true"` populated from the full curated list. Tags render as
colored chips in their stored `Color` within the picker and once selected,
matching how they'll appear elsewhere. If no groups/tags exist yet, the
picker is empty with placeholder text pointing at `/groups`.

This is purely configuration — no status information is introduced here,
consistent with the existing rule for this page.

## Dashboard changes

Two filter dropdowns above the domain table — "Group" and "Tag" — each a
single-select `MudSelect`, defaulting to "All domains." Selecting one
narrows `_rows` to domains that are a member of the selected Group/Tag
(via `DashboardSummary.Build`'s existing domain-list input — the filter
applies to the domain list before `Build` runs, so summary tile counts
reflect the filtered set too). Since a domain can belong to several
groups/tags, it reappears whenever any one of its groups/tags is selected —
no attempt to dedupe or merge filters; Group and Tag filters are
independent and both apply (AND) when both are set.

## Testing

Following this project's established `Testcontainers.PostgreSql` pattern
for anything touching the database:

- `Group`/`Tag` CRUD round-trips through `DotMarcDbContext` (create, rename,
  unique-index violation on duplicate name).
- Assigning/removing a domain's group and tag membership persists correctly
  through the many-to-many navigation.
- Deleting a Group/Tag with member domains removes only the membership
  rows, leaving the Domain and its Reports untouched.
- `DashboardSummary.Build`'s filtering behavior: a domain in multiple groups
  appears under each; the Group and Tag filters combine with AND; summary
  tile counts reflect the filtered set.
- No automated test for the Razor UI changes themselves, consistent with
  this project's established precedent (no Blazor component-rendering test
  framework).
