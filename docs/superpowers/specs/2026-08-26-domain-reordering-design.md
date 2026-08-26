# Domain Reordering Design

## Overview

Both `Dashboard.razor` and `ManageDomains.razor` currently list domains alphabetically by
name, with no way to change that order. This is the first of three planned domain-list
features (reordering, grouping, ownership/permissions — see the parent conversation for why
these were split into separate design/build cycles rather than one large change); this design
covers reordering only. It lets a user drag domains into a custom order on the Manage Domains
page, and has the Dashboard reflect that same order.

## Goals

- A custom, persisted display order for domains, settable by dragging rows on the Manage
  Domains page.
- The Dashboard's domain table reflects that same order — one saved order, not two
  independent ones.
- A domain added after some custom ordering already exists appends to the end, rather than
  jumping to the front.
- No manual data-backfill step required for existing installs — today's alphabetical order is
  preserved by default until someone actually reorders something.

## Non-goals

- Grouping or categorizing domains. A separate design, built after this one.
- Ownership/permissions. A separate, larger design, built after grouping.
- Drag-and-drop on the Dashboard itself — it displays the order Manage Domains sets, read-only.
  Duplicating drag-and-drop wiring across two tables wasn't judged worth it for a page that's
  otherwise a read-only health overview.
- MudBlazor's `MudDropContainer`/`MudDropZone` components. They're a generic drop-zone list
  system driven by an `Items`/`ItemRenderer` pair, not a `MudTable` row-drag feature — using
  them would mean replacing `ManageDomains.razor`'s table (columns, headers, hover) with a
  custom list layout. Native HTML5 drag-and-drop attributes on the existing `MudTable`'s cells
  keep the table exactly as it looks today.

## Data model

`Domain` gains one field: `SortOrder` (`int`). Requires an EF Core migration.

Existing rows get the database default (`0`) rather than a hand-authored backfill — every
query that orders by `SortOrder` also orders by `.ThenBy(d => d.Name)` as a secondary key, so
before anyone has dragged anything, every domain is still tied at `0` and the secondary key
reproduces exactly today's alphabetical order. Only once rows actually have distinct
`SortOrder` values (from a real reorder) does the primary key start doing anything — no
migration-time data rewrite needed.

A domain gets appended, not inserted at the front, at creation. Both places a `Domain` row is
created set `SortOrder` to one more than the current maximum:
- `DomainManagementService.AddDomainAsync` (manual add via Manage Domains).
- `PollingService.StoreReportAsync` (auto-discovery from an incoming report's first-seen
  domain).

`(await context.Domains.MaxAsync(d => (int?)d.SortOrder) ?? -1) + 1` handles both the
"domains already exist" and "this is the very first domain" cases with one expression — a
nullable `int?` projection is required for `MaxAsync` to return `null` (rather than throwing)
when the table is empty.

## `DomainManagementService.ReorderAsync`

New method, following this service's existing static-method-over-a-caller-supplied-context
pattern (same shape as `SetPinnedAsync`/`RemoveDomainAsync`):

```csharp
public static async Task ReorderAsync(DotMarcDbContext context, IReadOnlyList<int> orderedDomainIds, CancellationToken cancellationToken = default)
```

Takes the full ordered list of domain IDs (not just the moved item and its new position) and
writes `SortOrder = index` for every domain in that list, in one `SaveChangesAsync` call. A
full-list resequence rather than a gap/fractional-index scheme — simple, and correct at the
scale of a handful to a few dozen domains this app is designed for (no need for the kind of
fractional-ordering machinery that matters at thousands of rows).

## `ManageDomains.razor` — the drag UI

A new leading column in the existing `MudTable`, before "Domain": a drag-handle cell
containing a `DragIndicator` icon, `cursor: grab`, `draggable="true"`, with `@ondragstart`
capturing which domain is being dragged.

`@ondragover:preventDefault="true"` (Blazor's preventDefault modifier works standalone,
without needing an actual `@ondragover` handler) plus `@ondrop` calling into the reorder logic
are added to both the handle cell and the domain-name cell — a reasonably sized drop target
without repeating the same two attributes across all five columns of every row.

On drop: the in-memory `_domains` list is reordered (the dragged item removed from its old
index, inserted at the target's index), then `DomainManagementService.ReorderAsync` is called
with the resulting full ID order, then the page reloads from the database — so what's on
screen always matches what's persisted, rather than trusting the client-side reorder alone.

## `Dashboard.razor`

Its domain-loading query's in-memory sort changes from `.OrderBy(r => r.Name)` to
`.OrderBy(r => r.SortOrder).ThenBy(r => r.Name)`. No drag capability is added here — it's the
same read-only table it is today, just sorted differently.

## Testing

Following the existing test suite's `Testcontainers.PostgreSql` pattern:

- `ReorderAsync` writes `SortOrder` values matching the position of each ID in the list passed
  in, for a realistic multi-domain set.
- A domain added via `AddDomainAsync` after other domains already have distinct `SortOrder`
  values gets appended (its `SortOrder` is one more than the current maximum), not `0`.
- The equivalent append-at-end regression test for `PollingService.StoreReportAsync`'s
  auto-discovery path — a domain auto-discovered from an incoming report, when other domains
  already have distinct `SortOrder` values, appends rather than jumping to the front.
- Two domains with tied `SortOrder` (the untouched default) sort by name as the secondary key
  — regression coverage for "existing installs don't need a backfill."
- No automated test for `ManageDomains.razor`'s drag markup/event wiring itself, consistent
  with this project's established precedent (no Blazor component-rendering test framework
  anywhere in the suite) — verified by code review and, when the environment allows it, a
  manual check.
