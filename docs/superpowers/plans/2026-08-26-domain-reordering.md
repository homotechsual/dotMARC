# Domain Reordering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user drag domains into a custom order on the Manage Domains page, with the Dashboard reflecting that same saved order.

**Architecture:** A new `Domain.SortOrder` column, defaulted to `0` for existing rows (no data backfill — every query adds `.ThenBy(d => d.Name)` so ties reproduce today's alphabetical order until something is actually reordered). `DomainManagementService.ReorderAsync` persists a full resequence from a given ID order. Both domain-creation paths (manual add, auto-discovery) append new domains at `max(SortOrder) + 1` rather than `0`. `ManageDomains.razor` gets native HTML5 drag-and-drop on its existing `MudTable` rows (no MudBlazor drop-zone components — those would mean replacing the table with a custom list layout); `Dashboard.razor` just sorts by the same column, read-only.

**Tech Stack:** ASP.NET Core Blazor Server, MudBlazor 9.8.0, EF Core + Npgsql, xUnit + Testcontainers.PostgreSql (existing stack — no new dependency).

## Global Constraints

- `Domain.SortOrder` (`int`) is the new column. No data-backfill migration — rely on the DB default (`0`) plus `.ThenBy(d => d.Name)` everywhere `SortOrder` is used for ordering.
- A domain gets `SortOrder = (max existing SortOrder) + 1` at creation, in both `DomainManagementService.AddDomainAsync` and `PollingService.StoreReportAsync` — never `0`, which would jump it to the front of an existing custom order.
- `DomainManagementService.ReorderAsync` takes the *full* ordered list of domain IDs and resequences all of them in one save — not a partial/gap-based scheme.
- No MudBlazor `MudDropContainer`/`MudDropZone` — drag-and-drop is native HTML5 drag events on the existing `MudTable`'s cells, keeping the table's current columns/headers/hover unchanged.
- Dashboard has no drag capability — it displays whatever order Manage Domains set.
- No automated test for `ManageDomains.razor`'s drag markup/event wiring or for `Dashboard.razor`'s display — matches this project's established precedent (no Blazor component-rendering test framework anywhere in the suite).

---

### Task 1: `Domain.SortOrder` and `DomainManagementService.ReorderAsync`

**Files:**
- Modify: `src/DotMarc/Data/Domain.cs`
- Modify: `src/DotMarc/Data/DomainManagementService.cs`
- Modify: `test/DotMarc.Tests/Data/DomainManagementServiceTests.cs`
- (generated) `src/DotMarc/Migrations/` — a new EF Core migration

**Interfaces:**
- Produces: `Domain.SortOrder : int` and `DomainManagementService.ReorderAsync(DotMarcDbContext context, IReadOnlyList<int> orderedDomainIds, CancellationToken cancellationToken = default) : Task` — used by Task 3 (`ManageDomains.razor`). `AddDomainAsync`'s append-at-end behavior — used implicitly by Task 4 (nothing calls it directly, but Dashboard's sort depends on it being correct).

- [ ] **Step 1: Write the failing tests**

Add to `test/DotMarc.Tests/Data/DomainManagementServiceTests.cs`, inside the existing `DomainManagementServiceTests` class:

```csharp
    [Fact]
    public async Task ReorderAsync_SetsSortOrderToMatchTheGivenSequence()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, "a.com", CancellationToken.None);
        await DomainManagementService.AddDomainAsync(context, "b.com", CancellationToken.None);
        await DomainManagementService.AddDomainAsync(context, "c.com", CancellationToken.None);

        var domains = context.Domains.OrderBy(d => d.Name).ToList();
        var a = domains.Single(d => d.Name == "a.com");
        var b = domains.Single(d => d.Name == "b.com");
        var c = domains.Single(d => d.Name == "c.com");

        await DomainManagementService.ReorderAsync(context, [c.Id, a.Id, b.Id], CancellationToken.None);

        using var verify = CreateContext();
        Assert.Equal(0, verify.Domains.Single(d => d.Name == "c.com").SortOrder);
        Assert.Equal(1, verify.Domains.Single(d => d.Name == "a.com").SortOrder);
        Assert.Equal(2, verify.Domains.Single(d => d.Name == "b.com").SortOrder);
    }

    [Fact]
    public async Task AddDomainAsync_AppendsToTheEnd_WhenOtherDomainsAlreadyHaveDistinctSortOrder()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, "a.com", CancellationToken.None);
        await DomainManagementService.AddDomainAsync(context, "b.com", CancellationToken.None);
        var existing = context.Domains.OrderBy(d => d.Name).ToList();
        await DomainManagementService.ReorderAsync(context, [existing[1].Id, existing[0].Id], CancellationToken.None);

        await DomainManagementService.AddDomainAsync(context, "c.com", CancellationToken.None);

        using var verify = CreateContext();
        Assert.Equal(2, verify.Domains.Single(d => d.Name == "c.com").SortOrder);
    }

    [Fact]
    public void DomainsWithTiedSortOrder_SortByNameAsTheSecondaryKey()
    {
        // Regression coverage for "existing installs don't need a data-backfill migration": rows
        // created directly (bypassing AddDomainAsync's append-at-end logic), the way every domain
        // that predates this feature exists today, are left at SortOrder's default of 0 — tied.
        // The ordering query's secondary key must still produce a sensible, predictable order.
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "zebra.com", FirstSeenUtc = DateTimeOffset.UtcNow });
        context.Domains.Add(new Domain { Name = "apple.com", FirstSeenUtc = DateTimeOffset.UtcNow });
        context.Domains.Add(new Domain { Name = "mango.com", FirstSeenUtc = DateTimeOffset.UtcNow });
        context.SaveChanges();

        var ordered = context.Domains.OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select(d => d.Name).ToList();

        Assert.Equal(["apple.com", "mango.com", "zebra.com"], ordered);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "ReorderAsync_SetsSortOrderToMatchTheGivenSequence|AddDomainAsync_AppendsToTheEnd_WhenOtherDomainsAlreadyHaveDistinctSortOrder"`
Expected: FAIL to build — `Domain.SortOrder` and `DomainManagementService.ReorderAsync` don't exist yet.

- [ ] **Step 3: Add the `SortOrder` field**

In `src/DotMarc/Data/Domain.cs`, add a property alongside the existing ones:

```csharp
    public int SortOrder { get; set; }
```

- [ ] **Step 4: Make `AddDomainAsync` append at the end**

In `src/DotMarc/Data/DomainManagementService.cs`, change:

```csharp
        context.Domains.Add(new Domain { Name = normalized, FirstSeenUtc = DateTimeOffset.UtcNow, IsPinned = true });
```

to:

```csharp
        var nextSortOrder = (await context.Domains.MaxAsync(d => (int?)d.SortOrder, cancellationToken).ConfigureAwait(false) ?? -1) + 1;
        context.Domains.Add(new Domain { Name = normalized, FirstSeenUtc = DateTimeOffset.UtcNow, IsPinned = true, SortOrder = nextSortOrder });
```

(The nullable `int?` projection is required for `MaxAsync` to return `null`, rather than throw, when there are no domains yet — `?? -1` then makes the very first domain land at `SortOrder = 0`.)

- [ ] **Step 5: Add `ReorderAsync`**

In `src/DotMarc/Data/DomainManagementService.cs`, add this method after `SetPinnedAsync`:

```csharp
    /// <summary>Persists a full custom display order: SortOrder is set to each domain's index in
    /// orderedDomainIds. A full-list resequence rather than a gap/fractional scheme — simple, and
    /// correct at the scale (a handful to a few dozen domains) this app is designed for.</summary>
    public static async Task ReorderAsync(DotMarcDbContext context, IReadOnlyList<int> orderedDomainIds, CancellationToken cancellationToken = default)
    {
        var domains = await context.Domains
            .Where(d => orderedDomainIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, cancellationToken)
            .ConfigureAwait(false);

        for (var index = 0; index < orderedDomainIds.Count; index++)
        {
            domains[orderedDomainIds[index]].SortOrder = index;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
```

- [ ] **Step 6: Generate the EF Core migration**

Run: `dotnet ef migrations add AddDomainSortOrder --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj`
Expected: creates a new migration under `src/DotMarc/Migrations/` adding the `SortOrder` column (`integer NOT NULL DEFAULT 0`) to the `Domains` table, plus an updated `DotMarcDbContextModelSnapshot.cs`. Review the generated `Up`/`Down` methods to confirm — do not hand-edit the generated files unless something is actually wrong.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "ReorderAsync_SetsSortOrderToMatchTheGivenSequence|AddDomainAsync_AppendsToTheEnd_WhenOtherDomainsAlreadyHaveDistinctSortOrder"`
Expected: PASS.

- [ ] **Step 8: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/DotMarc/Data/Domain.cs src/DotMarc/Data/DomainManagementService.cs src/DotMarc/Migrations/ test/DotMarc.Tests/Data/DomainManagementServiceTests.cs
git commit -m "Add Domain.SortOrder and DomainManagementService.ReorderAsync"
```

---

### Task 2: Auto-discovered domains append at the end too

**Files:**
- Modify: `src/DotMarc/Ingestion/PollingService.cs`
- Modify: `test/DotMarc.Tests/Ingestion/PollingServiceTests.cs`

**Interfaces:**
- Consumes: `Domain.SortOrder` (Task 1).
- Produces: nothing new — this closes the second (of two) domain-creation path from Global Constraints' append-at-end rule.

- [ ] **Step 1: Write the failing test**

Add to `test/DotMarc.Tests/Ingestion/PollingServiceTests.cs`, inside the existing `PollingServiceTests` class (the file already has `using DotMarc.Data;` at the top):

```csharp
    [Fact]
    public async Task PollOnceAsync_AppendsNewlyDiscoveredDomain_AfterExistingCustomOrder()
    {
        using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, "existing-a.com", CancellationToken.None);
            await DomainManagementService.AddDomainAsync(context, "existing-b.com", CancellationToken.None);
            var existing = context.Domains.OrderBy(d => d.Name).ToList();
            await DomainManagementService.ReorderAsync(context, [existing[1].Id, existing[0].Id], CancellationToken.None);
        }

        var graphClient = new FakeGraphMailboxClient();
        graphClient.UnreadMessages.Add(new MailboxMessage("msg-1", "Report domain: contoso.io", true));
        graphClient.Attachments["msg-1"] = [new MailboxAttachment("report.xml.gz", "application/gzip", GzipOf(ValidReportXml))];

        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
            await service.PollOnceAsync(CancellationToken.None);
        }

        using var verify = CreateContext();
        var newDomain = verify.Domains.Single(d => d.Name == "contoso.io");
        Assert.Equal(2, newDomain.SortOrder);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter PollOnceAsync_AppendsNewlyDiscoveredDomain_AfterExistingCustomOrder`
Expected: FAIL — the newly-created domain's `SortOrder` is `0` (the field's default), not `2`.

- [ ] **Step 3: Write the implementation**

In `src/DotMarc/Ingestion/PollingService.cs`'s `StoreReportAsync`, change:

```csharp
        var domain = await context.Domains.SingleOrDefaultAsync(d => d.Name == parsed.Domain, cancellationToken).ConfigureAwait(false);
        if (domain is null)
        {
            domain = new Domain { Name = parsed.Domain, FirstSeenUtc = DateTimeOffset.UtcNow };
            context.Domains.Add(domain);
        }
```

to:

```csharp
        var domain = await context.Domains.SingleOrDefaultAsync(d => d.Name == parsed.Domain, cancellationToken).ConfigureAwait(false);
        if (domain is null)
        {
            var nextSortOrder = (await context.Domains.MaxAsync(d => (int?)d.SortOrder, cancellationToken).ConfigureAwait(false) ?? -1) + 1;
            domain = new Domain { Name = parsed.Domain, FirstSeenUtc = DateTimeOffset.UtcNow, SortOrder = nextSortOrder };
            context.Domains.Add(domain);
        }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter PollOnceAsync_AppendsNewlyDiscoveredDomain_AfterExistingCustomOrder`
Expected: PASS.

- [ ] **Step 5: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Ingestion/PollingService.cs test/DotMarc.Tests/Ingestion/PollingServiceTests.cs
git commit -m "Append auto-discovered domains after any existing custom order"
```

---

### Task 3: Drag-to-reorder on Manage Domains

**Files:**
- Modify: `src/DotMarc/Components/Pages/ManageDomains.razor`

**Interfaces:**
- Consumes: `DomainManagementService.ReorderAsync` (Task 1).

This task has no automated tests: consistent with this file's own prior UI-only changes (this project has no Blazor component-rendering test framework). Verification is a build check plus a manual check when the environment allows one.

- [ ] **Step 1: Sort by the custom order**

In `src/DotMarc/Components/Pages/ManageDomains.razor`'s `LoadAsync`, change:

```csharp
        _domains = await db.Domains
            .AsNoTracking()
            .OrderBy(d => d.Name)
            .Select(d => new DomainRow(d.Id, d.Name, d.IsPinned, d.Reports.Count, d.LastReportReceivedUtc))
            .ToListAsync();
```

to:

```csharp
        _domains = await db.Domains
            .AsNoTracking()
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .Select(d => new DomainRow(d.Id, d.Name, d.IsPinned, d.Reports.Count, d.LastReportReceivedUtc))
            .ToListAsync();
```

(Without this, reloading after a drop would immediately re-sort the list back to alphabetical, undoing the drag.)

- [ ] **Step 2: Add the drag-handle column and drop targets**

Change the `MudTable`'s `HeaderContent` from:

```razor
        <HeaderContent>
            <MudTh>Domain</MudTh>
            <MudTh>Pinned</MudTh>
            <MudTh>Reports</MudTh>
            <MudTh>Last report</MudTh>
            <MudTh></MudTh>
        </HeaderContent>
```

to:

```razor
        <HeaderContent>
            <MudTh></MudTh>
            <MudTh>Domain</MudTh>
            <MudTh>Pinned</MudTh>
            <MudTh>Reports</MudTh>
            <MudTh>Last report</MudTh>
            <MudTh></MudTh>
        </HeaderContent>
```

Change the `RowTemplate` from:

```razor
        <RowTemplate>
            <MudTd>@context.Name</MudTd>
            <MudTd>
                <MudSwitch T="bool" Value="context.IsPinned" ValueChanged="@(v => SetPinnedAsync(context, v))" Color="Color.Primary" />
            </MudTd>
            <MudTd>@context.ReportCount</MudTd>
            <MudTd>@(context.LastReportReceivedUtc is { } last ? last.ToString("g") : "never")</MudTd>
            <MudTd>
                <MudIconButton Icon="@Icons.Material.Filled.Delete" Color="Color.Error" OnClick="@(() => DeleteDomainAsync(context))" />
            </MudTd>
        </RowTemplate>
```

to:

```razor
        <RowTemplate>
            <MudTd draggable="true" @ondragstart="@(() => OnDragStart(context.Id))"
                   @ondragover:preventDefault="true" @ondrop="@(() => OnDropAsync(context.Id))"
                   Style="cursor:grab; width: 2.5rem;">
                <MudIcon Icon="@Icons.Material.Filled.DragIndicator" Size="Size.Small" />
            </MudTd>
            <MudTd @ondragover:preventDefault="true" @ondrop="@(() => OnDropAsync(context.Id))">@context.Name</MudTd>
            <MudTd>
                <MudSwitch T="bool" Value="context.IsPinned" ValueChanged="@(v => SetPinnedAsync(context, v))" Color="Color.Primary" />
            </MudTd>
            <MudTd>@context.ReportCount</MudTd>
            <MudTd>@(context.LastReportReceivedUtc is { } last ? last.ToString("g") : "never")</MudTd>
            <MudTd>
                <MudIconButton Icon="@Icons.Material.Filled.Delete" Color="Color.Error" OnClick="@(() => DeleteDomainAsync(context))" />
            </MudTd>
        </RowTemplate>
```

(`@ondragover:preventDefault="true"` works standalone in Blazor without needing a paired `@ondragover` handler — the browser only allows a drop on an element whose `dragover` default was prevented, so this is required on any cell that should accept a drop. The handle cell and the name cell both accept drops, giving a reasonably-sized target without repeating the two attributes across all six columns.)

- [ ] **Step 3: Add the drag handlers**

In the `@code` block, add these two methods and one field, near the other handlers (e.g. after `DeleteDomainAsync`):

```csharp
    private int? _draggedDomainId;

    private void OnDragStart(int domainId) => _draggedDomainId = domainId;

    private async Task OnDropAsync(int targetDomainId)
    {
        if (_domains is null || _draggedDomainId is null || _draggedDomainId == targetDomainId)
        {
            _draggedDomainId = null;
            return;
        }

        var draggedIndex = _domains.FindIndex(d => d.Id == _draggedDomainId);
        var targetIndex = _domains.FindIndex(d => d.Id == targetDomainId);
        _draggedDomainId = null;

        if (draggedIndex < 0 || targetIndex < 0)
        {
            return;
        }

        var moved = _domains[draggedIndex];
        _domains.RemoveAt(draggedIndex);
        _domains.Insert(targetIndex, moved);

        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            await DomainManagementService.ReorderAsync(db, _domains.Select(d => d.Id).ToList(), CancellationToken.None);
        }
        catch (Exception)
        {
            Snackbar.Add("Failed to save the new order. Try again.", Severity.Error);
        }

        await LoadAsync();
    }
```

(`LoadAsync` always runs afterward, success or failure — matching this file's existing `SetPinnedAsync` pattern — so what's on screen always ends up matching what's actually persisted, rather than trusting the client-side reorder alone if the save silently failed.)

- [ ] **Step 4: Build to confirm it compiles**

Run: `dotnet build dotMARC.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 5: Manual verification**

If the environment allows running the app (`docker compose up postgres` plus `dotnet run --project src/DotMarc/DotMarc.csproj` with the Graph/EntraId env vars set, per the README's Development section) and signing in: on `/domains` with at least three domains, confirm dragging a row by its handle to a new position moves it, that reloading the page keeps the new order, and that `/dashboard` shows domains in that same order. If the environment doesn't allow this (a known, previously-hit limitation in this project's sandboxed environments — no local Postgres port available, no interactive Entra sign-in), report clearly in your report which steps you could and couldn't perform, and why — this is an acceptable, expected limitation, not a blocker.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Components/Pages/ManageDomains.razor
git commit -m "Add drag-to-reorder on the Manage Domains page"
```

---

### Task 4: Dashboard reflects the custom order

**Files:**
- Modify: `src/DotMarc/Components/Pages/Dashboard.razor`

**Interfaces:**
- Consumes: `Domain.SortOrder` (Task 1).

No automated tests, same reasoning as Task 3.

- [ ] **Step 1: Sort by the custom order**

In `src/DotMarc/Components/Pages/Dashboard.razor`'s `LoadAsync`, change:

```csharp
        _rows = domains.Select(d =>
        {
            var passRate = DomainStatistics.GetPassRate(d.Reports);

            var missingReport = d.IsPinned && (d.LastReportReceivedUtc is null || d.LastReportReceivedUtc < DateTimeOffset.UtcNow.AddDays(-2));
            var status = missingReport ? "Missing" : passRate is null or >= 0.95 ? "OK" : "Warning";
            var color = status switch { "Missing" => Color.Error, "Warning" => Color.Warning, _ => Color.Success };

            return new DomainRow(d.Id, d.Name, status, color, passRate, d.LastReportReceivedUtc, d.IsPinned);
        }).OrderBy(r => r.Name).ToList();
```

to:

```csharp
        _rows = domains
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .Select(d =>
            {
                var passRate = DomainStatistics.GetPassRate(d.Reports);

                var missingReport = d.IsPinned && (d.LastReportReceivedUtc is null || d.LastReportReceivedUtc < DateTimeOffset.UtcNow.AddDays(-2));
                var status = missingReport ? "Missing" : passRate is null or >= 0.95 ? "OK" : "Warning";
                var color = status switch { "Missing" => Color.Error, "Warning" => Color.Warning, _ => Color.Success };

                return new DomainRow(d.Id, d.Name, status, color, passRate, d.LastReportReceivedUtc, d.IsPinned);
            }).ToList();
```

(Ordering moves before `Select` so it sorts by the source `Domain` entities' `SortOrder`, not by the projected `DomainRow`, which doesn't carry that field.)

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build dotMARC.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 3: Manual verification**

If the environment allows it (same setup as Task 3's manual step): after reordering domains on `/domains`, confirm `/dashboard`'s table shows them in that same order. If not possible in this environment, report which steps were skipped and why — expected, not a blocker.

- [ ] **Step 4: Commit**

```bash
git add src/DotMarc/Components/Pages/Dashboard.razor
git commit -m "Sort the Dashboard's domain table by the custom order"
```
