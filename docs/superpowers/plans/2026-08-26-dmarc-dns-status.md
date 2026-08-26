# DMARC DNS Status Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Track, per domain, whether its DMARC DNS records are actually correctly in place — its own `_dmarc.<domain>` record and, when required, the RFC 7489 external-reporting authorization record at `<domain>._report._dmarc.<mailbox-domain>` — queried against Cloudflare, refreshed automatically, summarized on the Dashboard and detailed on the per-domain drilldown. Bundled: renaming `IsPinned` → `IsMonitored` throughout.

**Architecture:** A waterfall check (`IDmarcDnsChecker`/`DmarcDnsChecker`, a typed `HttpClient` against Cloudflare's DNS-over-HTTPS JSON API, following the existing `IGraphMailboxClient`/`GraphMailboxClient` pattern) writes a `DmarcCheckStatus`/`DmarcCheckedUtc`/`DmarcCheckDetail` triple onto `Domain`. A new, independently-locked cycle inside `PollingService` (`RunDmarcCheckCycleAsync`, its own advisory lock key, called from `ExecuteAsync` alongside the existing poll cycle — deliberately NOT added as parameters to the existing `RunPollCycleAsync`, to avoid touching its five existing test call sites) re-checks any domain whose `DmarcCheckedUtc` is null or more than 24 hours old. `Dashboard.razor` and `DomainDetail.razor` both display the result via a small shared presentation helper; `ManageDomains.razor` is untouched beyond the rename, since it's a configuration surface, not a status one.

**Tech Stack:** ASP.NET Core Blazor Server, MudBlazor 9.8.0, EF Core + Npgsql, xUnit + Testcontainers.PostgreSql (existing stack — no new dependency; DNS queries go over plain `HttpClient`, no DNS protocol library).

## Global Constraints

- The check is a waterfall (own record → validity/rua match → same-domain exemption →
  authorization record), not two independent lookups — a domain missing its own record costs one
  DNS query, not two.
- Cloudflare's DNS-over-HTTPS JSON API only (`https://cloudflare-dns.com/dns-query?name=...&type=TXT`,
  header `Accept: application/dns-json`) — no raw UDP DNS, no other provider, no fallback provider.
- A failed check (network error, non-2xx, etc.) for a domain leaves that domain's
  `DmarcCheckStatus`/`DmarcCheckedUtc`/`DmarcCheckDetail` completely unchanged — no partial write,
  no distinct "error" status. It's simply retried next cycle.
- The 24-hour re-check gate and the DMARC-check cycle itself must not require changes to
  `PollingService.RunPollCycleAsync`'s signature or any of its five existing test call sites — see
  Task 4.
- `ManageDomains.razor` gets no DMARC status display of any kind — only the `IsMonitored` rename.
- Migrations are historical records; a rename is a new migration (`RenameColumn`), never an edit to
  an old one. Task 1's migration specifically must preserve existing data — see that task's
  migration step for why the default `dotnet ef migrations add` output can't be trusted blindly
  here the way it normally can for a brand-new column.

---

### Task 1: Rename `IsPinned` → `IsMonitored`

**Files:**
- Modify: `src/DotMarc/Data/Domain.cs`
- Modify: `src/DotMarc/Data/DomainManagementService.cs`
- Modify: `src/DotMarc/Components/Pages/Dashboard.razor`
- Modify: `src/DotMarc/Components/Pages/ManageDomains.razor`
- Modify: `test/DotMarc.Tests/Data/DomainManagementServiceTests.cs`
- Modify: `test/DotMarc.Tests/Data/DotMarcDbContextTests.cs`
- Modify: `test/DotMarc.Tests/Ingestion/PollingServiceTests.cs`
- (generated) `src/DotMarc/Migrations/` — a new EF Core migration

**Interfaces:**
- Produces: `Domain.IsMonitored` (replaces `IsPinned`), `DomainManagementService.SetMonitoredAsync`
  (replaces `SetPinnedAsync`) — used everywhere the old names were used, and by every later task in
  this plan that touches `Domain`.

This is a pure rename with no behavior change — every step below is a mechanical find/replace at
the named location. No new test content; existing tests are renamed in place and must still pass.

- [ ] **Step 1: `src/DotMarc/Data/Domain.cs`**

Change:
```csharp
/// <summary>A monitored domain. Rows are created automatically the first time a report arrives
/// for a domain (auto-discovery); <see cref="IsPinned"/> is set explicitly via the dashboard and
/// only affects whether a missing-report warning is shown for that domain — it does not change
/// ingestion behavior.</summary>
public sealed class Domain
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool IsPinned { get; set; }
```
to:
```csharp
/// <summary>A monitored domain. Rows are created automatically the first time a report arrives
/// for a domain (auto-discovery); <see cref="IsMonitored"/> is set explicitly via the dashboard
/// and only affects whether a missing-report warning is shown for that domain — it does not
/// change ingestion behavior.</summary>
public sealed class Domain
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool IsMonitored { get; set; }
```

- [ ] **Step 2: `src/DotMarc/Data/DomainManagementService.cs`**

Change the class doc comment's first word:
```csharp
/// <summary>Add/remove/pin operations for Domain rows created through the "Manage domains" page,
```
to:
```csharp
/// <summary>Add/remove/monitor operations for Domain rows created through the "Manage domains" page,
```

Change:
```csharp
    /// <summary>Creates a pinned Domain row with no reports yet, so it immediately shows as
    /// "Missing" on the Dashboard (Dashboard.razor's existing IsPinned &amp;&amp;
    /// LastReportReceivedUtc-is-null check) until its first real report arrives.</summary>
```
to:
```csharp
    /// <summary>Creates a monitored Domain row with no reports yet, so it immediately shows as
    /// "Missing" on the Dashboard (Dashboard.razor's existing IsMonitored &amp;&amp;
    /// LastReportReceivedUtc-is-null check) until its first real report arrives.</summary>
```

Change:
```csharp
        context.Domains.Add(new Domain { Name = normalized, FirstSeenUtc = DateTimeOffset.UtcNow, IsPinned = true, SortOrder = nextSortOrder });
```
to:
```csharp
        context.Domains.Add(new Domain { Name = normalized, FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = true, SortOrder = nextSortOrder });
```

Change:
```csharp
    public static async Task SetPinnedAsync(DotMarcDbContext context, int domainId, bool isPinned, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        domain.IsPinned = isPinned;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
```
to:
```csharp
    public static async Task SetMonitoredAsync(DotMarcDbContext context, int domainId, bool isMonitored, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        domain.IsMonitored = isMonitored;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
```

- [ ] **Step 3: `src/DotMarc/Components/Pages/Dashboard.razor`**

Change the header cell:
```razor
            <MudTh>Pinned</MudTh>
```
to:
```razor
            <MudTh>Monitored</MudTh>
```

Change:
```razor
            <MudTd>
                <MudSwitch T="bool" Value="context.IsPinned" ValueChanged="@(v => TogglePinnedAsync(context, v))" Color="Color.Primary" />
            </MudTd>
```
to:
```razor
            <MudTd>
                <MudSwitch T="bool" Value="context.IsMonitored" ValueChanged="@(v => ToggleMonitoredAsync(context, v))" Color="Color.Primary" />
            </MudTd>
```

Change:
```csharp
                var missingReport = d.IsPinned && (d.LastReportReceivedUtc is null || d.LastReportReceivedUtc < DateTimeOffset.UtcNow.AddDays(-2));
```
to:
```csharp
                var missingReport = d.IsMonitored && (d.LastReportReceivedUtc is null || d.LastReportReceivedUtc < DateTimeOffset.UtcNow.AddDays(-2));
```

Change:
```csharp
                return new DomainRow(d.Id, d.Name, status, color, passRate, d.LastReportReceivedUtc, d.IsPinned);
```
to:
```csharp
                return new DomainRow(d.Id, d.Name, status, color, passRate, d.LastReportReceivedUtc, d.IsMonitored);
```

Change:
```csharp
    private async Task TogglePinnedAsync(DomainRow row, bool isPinned)
    {
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            await DomainManagementService.SetPinnedAsync(db, row.Id, isPinned, CancellationToken.None);
        }
```
to:
```csharp
    private async Task ToggleMonitoredAsync(DomainRow row, bool isMonitored)
    {
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            await DomainManagementService.SetMonitoredAsync(db, row.Id, isMonitored, CancellationToken.None);
        }
```

Change:
```csharp
    private sealed record DomainRow(int Id, string Name, string Status, Color StatusColor, double? PassRate, DateTimeOffset? LastReportReceivedUtc, bool IsPinned);
```
to:
```csharp
    private sealed record DomainRow(int Id, string Name, string Status, Color StatusColor, double? PassRate, DateTimeOffset? LastReportReceivedUtc, bool IsMonitored);
```

- [ ] **Step 4: `src/DotMarc/Components/Pages/ManageDomains.razor`**

Change the header cell:
```razor
            <MudTh>Pinned</MudTh>
```
to:
```razor
            <MudTh>Monitored</MudTh>
```

Change:
```razor
            <MudTd>
                <MudSwitch T="bool" Value="context.IsPinned" ValueChanged="@(v => SetPinnedAsync(context, v))" Color="Color.Primary" />
            </MudTd>
```
to:
```razor
            <MudTd>
                <MudSwitch T="bool" Value="context.IsMonitored" ValueChanged="@(v => SetMonitoredAsync(context, v))" Color="Color.Primary" />
            </MudTd>
```

Change:
```csharp
        _domains = await db.Domains
            .AsNoTracking()
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .Select(d => new DomainRow(d.Id, d.Name, d.IsPinned, d.Reports.Count, d.LastReportReceivedUtc))
            .ToListAsync();
```
to:
```csharp
        _domains = await db.Domains
            .AsNoTracking()
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .Select(d => new DomainRow(d.Id, d.Name, d.IsMonitored, d.Reports.Count, d.LastReportReceivedUtc))
            .ToListAsync();
```

Change:
```csharp
    private async Task SetPinnedAsync(DomainRow row, bool isPinned)
    {
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            await DomainManagementService.SetPinnedAsync(db, row.Id, isPinned, CancellationToken.None);
        }
```
to:
```csharp
    private async Task SetMonitoredAsync(DomainRow row, bool isMonitored)
    {
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            await DomainManagementService.SetMonitoredAsync(db, row.Id, isMonitored, CancellationToken.None);
        }
```

Change:
```csharp
    private sealed record DomainRow(int Id, string Name, bool IsPinned, int ReportCount, DateTimeOffset? LastReportReceivedUtc);
```
to:
```csharp
    private sealed record DomainRow(int Id, string Name, bool IsMonitored, int ReportCount, DateTimeOffset? LastReportReceivedUtc);
```

- [ ] **Step 5: `test/DotMarc.Tests/Data/DomainManagementServiceTests.cs`**

Change `Assert.True(domain.IsPinned);` to `Assert.True(domain.IsMonitored);` (the assertion inside
`AddDomainAsync_CreatesAPinnedDomain_WithNormalizedName` — leave the test's own name as-is, it's
about `AddDomainAsync`'s behavior, not specifically about this field's name).

Change:
```csharp
    public async Task SetPinnedAsync_TogglesIsPinned()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, "contoso.com", CancellationToken.None);
        var domainId = context.Domains.Single().Id;

        await DomainManagementService.SetPinnedAsync(context, domainId, false, CancellationToken.None);

        using var verify = CreateContext();
        Assert.False(verify.Domains.Single().IsPinned);
    }
```
to:
```csharp
    public async Task SetMonitoredAsync_TogglesIsMonitored()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, "contoso.com", CancellationToken.None);
        var domainId = context.Domains.Single().Id;

        await DomainManagementService.SetMonitoredAsync(context, domainId, false, CancellationToken.None);

        using var verify = CreateContext();
        Assert.False(verify.Domains.Single().IsMonitored);
    }
```

- [ ] **Step 6: `test/DotMarc.Tests/Data/DotMarcDbContextTests.cs`**

Change `IsPinned = true,` to `IsMonitored = true,` and `Assert.True(savedDomain.IsPinned);` to
`Assert.True(savedDomain.IsMonitored);` (both inside `CanInsertAndQuery_DomainWithReportAndRecords`).

- [ ] **Step 7: `test/DotMarc.Tests/Ingestion/PollingServiceTests.cs`**

Change `Assert.True(domain.IsPinned);` to `Assert.True(domain.IsMonitored);` (inside
`PollOnceAsync_MatchesAManuallyAddedDomain_InsteadOfCreatingADuplicate`).

- [ ] **Step 8: Generate the migration**

Run: `dotnet ef migrations add RenameIsPinnedToIsMonitored --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj`

This is a rename of an existing column, not a new one — data loss matters here, since a live
deployment has real `Domain` rows with real `IsPinned` values that must survive. Open the generated
migration file and inspect its `Up()` method:

- If it contains `migrationBuilder.RenameColumn(name: "IsPinned", newName: "IsMonitored", table: "Domains");` (or an equivalent `RenameColumn` call), EF's scaffolder correctly detected this as a
  rename. Leave it exactly as generated — this is the expected, no-hand-editing case.
- If it instead contains a `DropColumn(name: "IsPinned", table: "Domains")` paired with an
  `AddColumn<bool>(name: "IsMonitored", table: "Domains", ...)`, EF treated this as
  remove-then-add rather than a rename — which would silently reset every existing domain's
  monitored flag to its default on a real deployment. Replace both calls in `Up()` with a single
  `migrationBuilder.RenameColumn(name: "IsPinned", newName: "IsMonitored", table: "Domains");`,
  and replace `Down()`'s corresponding calls with the reverse:
  `migrationBuilder.RenameColumn(name: "IsMonitored", newName: "IsPinned", table: "Domains");`.
  This is the one case in this project where hand-editing a generated migration is required, not
  merely reviewed — say explicitly in your report which case you hit.

- [ ] **Step 9: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS (all existing tests, under their renamed names where applicable).

- [ ] **Step 10: Commit**

```bash
git add src/DotMarc/Data/Domain.cs src/DotMarc/Data/DomainManagementService.cs src/DotMarc/Components/Pages/Dashboard.razor src/DotMarc/Components/Pages/ManageDomains.razor src/DotMarc/Migrations/ test/DotMarc.Tests/Data/DomainManagementServiceTests.cs test/DotMarc.Tests/Data/DotMarcDbContextTests.cs test/DotMarc.Tests/Ingestion/PollingServiceTests.cs
git commit -m "Rename Domain.IsPinned to IsMonitored"
```

---

### Task 2: `Domain` DMARC check fields

**Files:**
- Create: `src/DotMarc/Data/DmarcCheckStatus.cs`
- Modify: `src/DotMarc/Data/Domain.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs`
- Modify: `test/DotMarc.Tests/Data/DotMarcDbContextTests.cs`
- (generated) `src/DotMarc/Migrations/` — a new EF Core migration

**Interfaces:**
- Produces: `DotMarc.Data.DmarcCheckStatus` enum (`NotChecked`, `Ok`, `MissingOwnRecord`,
  `Misconfigured`, `MissingAuthorizationRecord` — in this order, so `NotChecked` is the C#/DB
  default) and `Domain.DmarcCheckStatus`/`DmarcCheckedUtc`/`DmarcCheckDetail` — used by Task 3
  (the checker returns a `DmarcCheckStatus`), Task 4 (writes these fields), and Tasks 5-6 (read
  them for display).

- [ ] **Step 1: Write the failing tests**

Add to `test/DotMarc.Tests/Data/DotMarcDbContextTests.cs`, inside the existing
`DotMarcDbContextTests` class:

```csharp
    [Fact]
    public void CanInsertAndQuery_DomainWithDmarcCheckFields()
    {
        using (var context = CreateContext())
        {
            context.Domains.Add(new Domain
            {
                Name = "contoso.io",
                FirstSeenUtc = DateTimeOffset.UtcNow,
                DmarcCheckStatus = DmarcCheckStatus.MissingAuthorizationRecord,
                DmarcCheckedUtc = DateTimeOffset.UtcNow,
                DmarcCheckDetail = "No TXT record found at contoso.io._report._dmarc.mjco.uk"
            });
            context.SaveChanges();
        }

        using (var verify = CreateContext())
        {
            var domain = verify.Domains.Single();
            Assert.Equal(DmarcCheckStatus.MissingAuthorizationRecord, domain.DmarcCheckStatus);
            Assert.NotNull(domain.DmarcCheckedUtc);
            Assert.Equal("No TXT record found at contoso.io._report._dmarc.mjco.uk", domain.DmarcCheckDetail);
        }
    }

    [Fact]
    public void Domain_DmarcCheckStatus_DefaultsToNotChecked()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        context.SaveChanges();

        using var verify = CreateContext();
        var domain = verify.Domains.Single();
        Assert.Equal(DmarcCheckStatus.NotChecked, domain.DmarcCheckStatus);
        Assert.Null(domain.DmarcCheckedUtc);
        Assert.Null(domain.DmarcCheckDetail);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "CanInsertAndQuery_DomainWithDmarcCheckFields|Domain_DmarcCheckStatus_DefaultsToNotChecked"`
Expected: FAIL to build — `DmarcCheckStatus` and the three `Domain` fields don't exist yet.

- [ ] **Step 3: Create the enum**

Create `src/DotMarc/Data/DmarcCheckStatus.cs`:

```csharp
namespace DotMarc.Data;

/// <summary>The result of the most recent DMARC DNS check for a Domain — see
/// DotMarc.Dns.DmarcDnsChecker for how each value is determined. NotChecked is listed first so it
/// is the enum's (and therefore the database column's) default value: an existing domain from
/// before this feature, or a domain that hasn't been checked yet, is NotChecked without needing
/// any data migration.</summary>
public enum DmarcCheckStatus
{
    NotChecked,
    Ok,
    MissingOwnRecord,
    Misconfigured,
    MissingAuthorizationRecord
}
```

- [ ] **Step 4: Add the fields to `Domain`**

In `src/DotMarc/Data/Domain.cs`, add three properties alongside the existing ones:

```csharp
    public DmarcCheckStatus DmarcCheckStatus { get; set; }
    public DateTimeOffset? DmarcCheckedUtc { get; set; }
    public string? DmarcCheckDetail { get; set; }
```

- [ ] **Step 5: Configure the enum-as-string conversion**

In `src/DotMarc/Data/DotMarcDbContext.cs`, change:

```csharp
        modelBuilder.Entity<Domain>(entity =>
        {
            entity.HasIndex(d => d.Name).IsUnique();
        });
```

to:

```csharp
        modelBuilder.Entity<Domain>(entity =>
        {
            entity.HasIndex(d => d.Name).IsUnique();
            entity.Property(d => d.DmarcCheckStatus).HasConversion<string>();
        });
```

(Matches this project's existing convention for enum columns — see `ReportRecord`'s
`Disposition`/`SpfResult`/`DkimResult` configuration a few lines below in the same file.)

- [ ] **Step 6: Generate the migration**

Run: `dotnet ef migrations add AddDomainDmarcCheckFields --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj`

Unlike Task 1, this is a brand-new column, so the usual "review, don't blindly trust" applies, not
hand-editing: confirm `Up()` adds `DmarcCheckStatus` (as `character varying`/`text`, not `integer`
— the string conversion from Step 5 must be reflected), `DmarcCheckedUtc` (nullable
`timestamp with time zone`), and `DmarcCheckDetail` (nullable `text`) to the `Domains` table, with
`DmarcCheckStatus`'s default matching the string form of `DmarcCheckStatus.NotChecked` (i.e.
`"NotChecked"`) so existing rows come through correctly without a manual backfill.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "CanInsertAndQuery_DomainWithDmarcCheckFields|Domain_DmarcCheckStatus_DefaultsToNotChecked"`
Expected: PASS.

- [ ] **Step 8: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/DotMarc/Data/DmarcCheckStatus.cs src/DotMarc/Data/Domain.cs src/DotMarc/Data/DotMarcDbContext.cs src/DotMarc/Migrations/ test/DotMarc.Tests/Data/DotMarcDbContextTests.cs
git commit -m "Add Domain DMARC check status fields"
```

---

### Task 3: `IDmarcDnsChecker` / `DmarcDnsChecker`

**Files:**
- Create: `src/DotMarc/Dns/IDmarcDnsChecker.cs`
- Create: `src/DotMarc/Dns/DmarcCheckResult.cs`
- Create: `src/DotMarc/Dns/DmarcDnsChecker.cs`
- Create: `test/DotMarc.Tests/Dns/DmarcDnsCheckerTests.cs`

**Interfaces:**
- Consumes: `DotMarc.Data.DmarcCheckStatus` (Task 2).
- Produces: `IDmarcDnsChecker.CheckAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken) : Task<DmarcCheckResult>` and `DmarcCheckResult(DmarcCheckStatus Status, string? Detail)` — used by Task 4.

This task is fully self-contained: no DI registration yet (Task 4), no `PollingService`
integration yet (Task 4) — just the checker itself, tested against a fake HTTP handler exactly the
way `GraphMailboxClient` is tested against one in `test/DotMarc.Tests/Graph/GraphMailboxClientTests.cs`.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Dns/DmarcDnsCheckerTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Dns;

public class DmarcDnsCheckerTests
{
    private static (DmarcDnsChecker checker, FakeHttpMessageHandler handler) CreateChecker()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new DmarcDnsChecker(http), handler);
    }

    private const string NxDomainResponse = """{"Status":3}""";

    [Fact]
    public async Task CheckAsync_ReturnsMissingOwnRecord_WhenNoDmarcRecordExists()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = NxDomainResponse;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.MissingOwnRecord, result.Status);
        Assert.Contains("_dmarc.contoso.io", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task CheckAsync_ReturnsMisconfigured_WhenRecordDoesNotStartWithVDmarc1()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"not a dmarc record\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Misconfigured, result.Status);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMisconfigured_WhenRuaDoesNotMatchTheConfiguredMailbox()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:other@example.com\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Misconfigured, result.Status);
        Assert.Contains("other@example.com", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_ReturnsOk_WithNoSecondQuery_WhenMailboxDomainMatchesTheMonitoredDomain()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:dmarc@contoso.io\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "dmarc@contoso.io", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Ok, result.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CheckAsync_ReturnsOk_WhenAuthorizationRecordIsPresent()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBodies.Enqueue("""
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk\""}]}
            """);
        handler.ResponseBodies.Enqueue("""
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1\""}]}
            """);

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Ok, result.Status);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("contoso.io._report._dmarc.mjco.uk", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task CheckAsync_ReturnsMissingAuthorizationRecord_WhenAuthorizationRecordIsAbsent()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBodies.Enqueue("""
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk\""}]}
            """);
        handler.ResponseBodies.Enqueue(NxDomainResponse);

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.MissingAuthorizationRecord, result.Status);
    }

    [Fact]
    public async Task CheckAsync_ParsesMultiSegmentTxtRecordValues()
    {
        var (checker, handler) = CreateChecker();
        // A long TXT value split across two quoted segments, as Cloudflare's JSON API returns for
        // records over 255 bytes.
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; \" \"rua=mailto:rua.dmarc@mjco.uk\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Ok, result.Status);
    }

    [Fact]
    public async Task CheckAsync_MatchesRuaAddress_AmongMultipleCommaSeparatedAddresses()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:other@example.com,mailto:rua.dmarc@mjco.uk\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Ok, result.Status);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DmarcDnsCheckerTests`
Expected: FAIL to build — `IDmarcDnsChecker`/`DmarcDnsChecker`/`DmarcCheckResult` don't exist yet.

- [ ] **Step 3: Write the implementation**

Create `src/DotMarc/Dns/DmarcCheckResult.cs`:

```csharp
using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>The outcome of one DmarcDnsChecker.CheckAsync call. Detail is null exactly when Status
/// is Ok — there's nothing to explain about a passing check.</summary>
public sealed record DmarcCheckResult(DmarcCheckStatus Status, string? Detail);
```

Create `src/DotMarc/Dns/IDmarcDnsChecker.cs`:

```csharp
namespace DotMarc.Dns;

public interface IDmarcDnsChecker
{
    Task<DmarcCheckResult> CheckAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken);
}
```

Create `src/DotMarc/Dns/DmarcDnsChecker.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>Checks whether a domain's DMARC records are correctly in place, querying Cloudflare's
/// DNS-over-HTTPS JSON API rather than whatever resolver the host happens to have configured — see
/// docs/superpowers/specs/2026-08-26-dmarc-dns-status-design.md for why. A waterfall, not two
/// independent lookups: each step only runs if the previous one passed, so a domain with no DMARC
/// record at all costs one query, not two.</summary>
public sealed class DmarcDnsChecker : IDmarcDnsChecker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public DmarcDnsChecker(HttpClient http) => _http = http;

    public async Task<DmarcCheckResult> CheckAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken)
    {
        var ownRecord = await QueryTxtAsync($"_dmarc.{domainName}", cancellationToken).ConfigureAwait(false);
        if (ownRecord is null)
        {
            return new DmarcCheckResult(DmarcCheckStatus.MissingOwnRecord, $"No TXT record found at _dmarc.{domainName}");
        }

        if (!ownRecord.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase))
        {
            return new DmarcCheckResult(DmarcCheckStatus.Misconfigured, $"_dmarc.{domainName} does not start with v=DMARC1: {ownRecord}");
        }

        var ruaAddresses = ParseRuaAddresses(ownRecord);
        if (!ruaAddresses.Any(a => string.Equals(a, mailboxAddress, StringComparison.OrdinalIgnoreCase)))
        {
            return new DmarcCheckResult(DmarcCheckStatus.Misconfigured,
                ruaAddresses.Count == 0
                    ? $"_dmarc.{domainName} has no rua= tag"
                    : $"_dmarc.{domainName}'s rua= points to {string.Join(", ", ruaAddresses)}, not {mailboxAddress}");
        }

        var mailboxDomain = mailboxAddress[(mailboxAddress.IndexOf('@') + 1)..];
        if (string.Equals(mailboxDomain, domainName, StringComparison.OrdinalIgnoreCase))
        {
            return new DmarcCheckResult(DmarcCheckStatus.Ok, null);
        }

        var authorizationName = $"{domainName}._report._dmarc.{mailboxDomain}";
        var authorizationRecord = await QueryTxtAsync(authorizationName, cancellationToken).ConfigureAwait(false);
        return authorizationRecord is null
            ? new DmarcCheckResult(DmarcCheckStatus.MissingAuthorizationRecord, $"No TXT record found at {authorizationName}")
            : new DmarcCheckResult(DmarcCheckStatus.Ok, null);
    }

    /// <summary>Returns the first TXT record's value (quotes stripped, multi-segment values
    /// joined), or null if the name doesn't resolve or has no TXT records (Cloudflare's JSON API
    /// omits Answer entirely for both NXDOMAIN and NODATA — no need to branch on Status).</summary>
    private async Task<string?> QueryTxtAsync(string name, CancellationToken cancellationToken)
    {
        var response = await _http.GetAsync($"dns-query?name={Uri.EscapeDataString(name)}&type=TXT", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;

        var answer = parsed.Answer?.FirstOrDefault();
        if (answer is null)
        {
            return null;
        }

        // Cloudflare's JSON API returns the TXT record's data as one or more double-quoted
        // segments (multiple only for a value over 255 bytes, split across DNS's own
        // character-string length limit) — e.g. "\"v=DMARC1; p=quarantine\"" for a short record,
        // or "\"first part\" \"second part\"" for a long one. Splitting on `" "` between quoted
        // segments and stripping the outer quotes from what's left reconstructs the original value.
        return string.Join("", answer.Data.Split("\" \"")).Trim('"');
    }

    private static List<string> ParseRuaAddresses(string record)
    {
        var ruaTag = record.Split(';')
            .Select(part => part.Trim())
            .FirstOrDefault(part => part.StartsWith("rua=", StringComparison.OrdinalIgnoreCase));

        if (ruaTag is null)
        {
            return [];
        }

        return ruaTag["rua=".Length..]
            .Split(',')
            .Select(uri => uri.Trim())
            .Where(uri => uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            .Select(uri => uri["mailto:".Length..])
            .ToList();
    }

    private sealed record DnsOverHttpsResponse(
        [property: JsonPropertyName("Status")] int Status,
        [property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("data")] string Data);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DmarcDnsCheckerTests`
Expected: PASS (8 tests).

- [ ] **Step 5: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Dns/ test/DotMarc.Tests/Dns/
git commit -m "Add IDmarcDnsChecker/DmarcDnsChecker"
```

---

### Task 4: Wire the checker into `PollingService`

**Files:**
- Modify: `src/DotMarc/Ingestion/PollingService.cs`
- Modify: `src/DotMarc/Program.cs`
- Create: `test/DotMarc.Tests/Internal/FakeDmarcDnsChecker.cs`
- Create: `test/DotMarc.Tests/Ingestion/DmarcCheckCycleTests.cs`

**Interfaces:**
- Consumes: `IDmarcDnsChecker`/`DmarcCheckResult` (Task 3), `Domain.DmarcCheckStatus`/`DmarcCheckedUtc`/`DmarcCheckDetail` (Task 2).
- Produces: `PollingService.RunDmarcCheckCycleAsync(DotMarcDbContext context, IDmarcDnsChecker dmarcChecker, string mailboxAddress, CancellationToken cancellationToken) : Task` and `PollingService.DmarcCheckLeaderLockKey` (internal const) — a new, independently-testable method, deliberately **not** a change to `RunPollCycleAsync`'s signature (see Global Constraints — that method has five existing test call sites this task must not touch).

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Internal/FakeDmarcDnsChecker.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Dns;

namespace DotMarc.Tests.Internal;

internal sealed class FakeDmarcDnsChecker : IDmarcDnsChecker
{
    public DmarcCheckResult Result { get; set; } = new(DmarcCheckStatus.Ok, null);
    public bool ShouldThrow { get; set; }
    public List<string> CheckedDomains { get; } = [];

    public Task<DmarcCheckResult> CheckAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken)
    {
        CheckedDomains.Add(domainName);
        if (ShouldThrow)
        {
            throw new HttpRequestException("Simulated Cloudflare failure.");
        }
        return Task.FromResult(Result);
    }
}
```

Create `test/DotMarc.Tests/Ingestion/DmarcCheckCycleTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.Ingestion;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace DotMarc.Tests.Ingestion;

[Collection("Postgres")]
public sealed class DmarcCheckCycleTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DmarcCheckCycleTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    private static PollingService CreateService(DotMarcDbContext context) =>
        new(new FakeGraphMailboxClient(), context, NullLogger<PollingService>.Instance);

    [Fact]
    public async Task RunDmarcCheckCycleAsync_ChecksADomainNeverCheckedBefore()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        var checker = new FakeDmarcDnsChecker { Result = new DmarcCheckResult(DmarcCheckStatus.MissingOwnRecord, "No TXT record found at _dmarc.contoso.io") };
        var service = CreateService(context);
        await service.RunDmarcCheckCycleAsync(context, checker, "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Contains("contoso.io", checker.CheckedDomains);
        var domain = context.Domains.Single();
        Assert.Equal(DmarcCheckStatus.MissingOwnRecord, domain.DmarcCheckStatus);
        Assert.Equal("No TXT record found at _dmarc.contoso.io", domain.DmarcCheckDetail);
        Assert.NotNull(domain.DmarcCheckedUtc);
    }

    [Fact]
    public async Task RunDmarcCheckCycleAsync_SkipsADomainCheckedRecently()
    {
        using var context = CreateContext();
        var recentCheck = DateTimeOffset.UtcNow.AddHours(-1);
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            DmarcCheckStatus = DmarcCheckStatus.Ok,
            DmarcCheckedUtc = recentCheck
        });
        await context.SaveChangesAsync();

        var checker = new FakeDmarcDnsChecker();
        var service = CreateService(context);
        await service.RunDmarcCheckCycleAsync(context, checker, "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Empty(checker.CheckedDomains);
        Assert.Equal(recentCheck, context.Domains.Single().DmarcCheckedUtc);
    }

    [Fact]
    public async Task RunDmarcCheckCycleAsync_RechecksADomainCheckedMoreThan24HoursAgo()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            DmarcCheckStatus = DmarcCheckStatus.MissingOwnRecord,
            DmarcCheckedUtc = DateTimeOffset.UtcNow.AddHours(-25)
        });
        await context.SaveChangesAsync();

        var checker = new FakeDmarcDnsChecker { Result = new DmarcCheckResult(DmarcCheckStatus.Ok, null) };
        var service = CreateService(context);
        await service.RunDmarcCheckCycleAsync(context, checker, "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Contains("contoso.io", checker.CheckedDomains);
        Assert.Equal(DmarcCheckStatus.Ok, context.Domains.Single().DmarcCheckStatus);
    }

    [Fact]
    public async Task RunDmarcCheckCycleAsync_LeavesStatusUnchanged_WhenTheCheckItselfThrows()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            DmarcCheckStatus = DmarcCheckStatus.MissingOwnRecord,
            DmarcCheckDetail = "No TXT record found at _dmarc.contoso.io"
        });
        await context.SaveChangesAsync();

        var checker = new FakeDmarcDnsChecker { ShouldThrow = true };
        var service = CreateService(context);
        await service.RunDmarcCheckCycleAsync(context, checker, "rua.dmarc@mjco.uk", CancellationToken.None);

        using var verify = CreateContext();
        var verifyDomain = verify.Domains.Single();
        Assert.Equal(DmarcCheckStatus.MissingOwnRecord, verifyDomain.DmarcCheckStatus);
        Assert.Equal("No TXT record found at _dmarc.contoso.io", verifyDomain.DmarcCheckDetail);
        Assert.Null(verifyDomain.DmarcCheckedUtc);
    }

    [Fact]
    public async Task RunDmarcCheckCycleAsync_SkipsEntirely_WhenAnotherInstanceHoldsTheLock()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", PollingService.DmarcCheckLeaderLockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }

        var checker = new FakeDmarcDnsChecker();
        var service = CreateService(context);
        await service.RunDmarcCheckCycleAsync(context, checker, "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Empty(checker.CheckedDomains);

        await lockTransaction.RollbackAsync();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DmarcCheckCycleTests`
Expected: FAIL to build — `RunDmarcCheckCycleAsync`/`DmarcCheckLeaderLockKey` don't exist yet.

- [ ] **Step 3: Write the implementation**

In `src/DotMarc/Ingestion/PollingService.cs`, add to the `using` block at the top:

```csharp
using DotMarc.Dns;
```

Add a new lock-key constant after the existing `PollingLeaderLockKey`:

```csharp
    /// <summary>Arbitrary fixed key for this service's DMARC-check advisory lock — distinct from
    /// PollingLeaderLockKey so the mailbox-poll cycle and the DMARC DNS-check cycle run under
    /// independent locks rather than being forced to share the same leader/timing.</summary>
    internal const long DmarcCheckLeaderLockKey = 84_200_003;
```

Change `ExecuteAsync` from:

```csharp
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options?.PollIntervalSeconds ?? 300);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_scopeFactory is not null)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var graphClient = scope.ServiceProvider.GetRequiredService<IGraphMailboxClient>();
                    var context = scope.ServiceProvider.GetRequiredService<DotMarcDbContext>();
                    await RunPollCycleAsync(graphClient, context, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Poll cycle failed; will retry next interval.");
            }

            await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
        }
    }
```

to:

```csharp
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options?.PollIntervalSeconds ?? 300);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_scopeFactory is not null)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var graphClient = scope.ServiceProvider.GetRequiredService<IGraphMailboxClient>();
                    var context = scope.ServiceProvider.GetRequiredService<DotMarcDbContext>();
                    await RunPollCycleAsync(graphClient, context, stoppingToken).ConfigureAwait(false);

                    var dmarcChecker = scope.ServiceProvider.GetRequiredService<IDmarcDnsChecker>();
                    await RunDmarcCheckCycleAsync(context, dmarcChecker, _options!.MailboxAddress, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Poll cycle failed; will retry next interval.");
            }

            await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
        }
    }
```

Add the new method after `RunPollCycleAsync` (before `RecordPollCycleAsync`):

```csharp
    /// <summary>Runs a DMARC DNS status check for every domain whose last check (DmarcCheckedUtc)
    /// is null or more than 24 hours old — independent of, and under a separate advisory lock from,
    /// the mailbox poll cycle above, since the two concerns don't need to share timing or a leader.
    /// A domain whose check itself fails (network error, Cloudflare unreachable) is left with its
    /// prior status/timestamp untouched and simply retried next cycle — matching this service's
    /// existing "leave it, retry later" policy for other kinds of per-item failure.</summary>
    internal async Task RunDmarcCheckCycleAsync(DotMarcDbContext context, IDmarcDnsChecker dmarcChecker, string mailboxAddress, CancellationToken cancellationToken)
    {
        var connectionString = context.Database.GetConnectionString()
            ?? throw new InvalidOperationException("DotMarcDbContext has no connection string configured.");

        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var lockTransaction = await lockConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        bool acquired;
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", DmarcCheckLeaderLockKey);
            acquired = (bool)(await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (!acquired)
        {
            _logger.LogDebug("Another instance already holds the DMARC-check lock for this cycle; skipping.");
            return;
        }

        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            var staleDomains = await context.Domains
                .Where(d => d.DmarcCheckedUtc == null || d.DmarcCheckedUtc < cutoff)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var anyUpdated = false;
            foreach (var domain in staleDomains)
            {
                DmarcCheckResult result;
                try
                {
                    result = await dmarcChecker.CheckAsync(domain.Name, mailboxAddress, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DMARC DNS check failed for {Domain}; will retry next cycle.", domain.Name);
                    continue;
                }

                domain.DmarcCheckStatus = result.Status;
                domain.DmarcCheckedUtc = DateTimeOffset.UtcNow;
                domain.DmarcCheckDetail = result.Detail;
                anyUpdated = true;
            }

            if (anyUpdated)
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await lockTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
```

In `src/DotMarc/Program.cs`, add this registration after the existing
`AddHttpClient<IGraphMailboxClient, GraphMailboxClient>` block:

```csharp
builder.Services.AddHttpClient<IDmarcDnsChecker, DmarcDnsChecker>(client =>
{
    client.BaseAddress = new Uri("https://cloudflare-dns.com/");
    client.DefaultRequestHeaders.Add("Accept", "application/dns-json");
});
```

Add `using DotMarc.Dns;` to `Program.cs`'s existing `using` block at the top (alongside the
existing `using DotMarc.Graph;`).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DmarcCheckCycleTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS — critically, all five pre-existing `RunPollCycleAsync`-based tests in
`PollingServiceLeaderLockTests.cs` still pass unmodified, confirming this task didn't touch that
method's signature.

- [ ] **Step 6: Build to confirm `Program.cs` wires up correctly**

Run: `dotnet build dotMARC.sln`
Expected: Build succeeds with no errors (this exercises the DI registration, which isn't otherwise
covered by the unit tests above since they construct `PollingService`/`DmarcDnsChecker` directly).

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Ingestion/PollingService.cs src/DotMarc/Program.cs test/DotMarc.Tests/Internal/FakeDmarcDnsChecker.cs test/DotMarc.Tests/Ingestion/DmarcCheckCycleTests.cs
git commit -m "Wire DMARC DNS checking into PollingService"
```

---

### Task 5: Dashboard shows DNS status

**Files:**
- Create: `src/DotMarc/Reporting/DmarcStatusPresentation.cs`
- Create: `test/DotMarc.Tests/Reporting/DmarcStatusPresentationTests.cs`
- Modify: `src/DotMarc/Components/Pages/Dashboard.razor`

**Interfaces:**
- Consumes: `Domain.DmarcCheckStatus` (Task 2).
- Produces: `DotMarc.Reporting.DmarcStatusPresentation.GetColor(DmarcCheckStatus) : MudBlazor.Color` and `.GetLabel(DmarcCheckStatus) : string` — used by this task and Task 6, following the same shared-presentation-logic precedent as `DomainStatistics` (see that class's own doc comment for why: two pages needing the same mapping is exactly the case it was extracted for).

- [ ] **Step 1: Write the failing test**

Create `test/DotMarc.Tests/Reporting/DmarcStatusPresentationTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Reporting;
using MudBlazor;
using Xunit;

namespace DotMarc.Tests.Reporting;

public sealed class DmarcStatusPresentationTests
{
    [Theory]
    [InlineData(DmarcCheckStatus.Ok, Color.Success, "OK")]
    [InlineData(DmarcCheckStatus.MissingOwnRecord, Color.Error, "No DMARC record")]
    [InlineData(DmarcCheckStatus.Misconfigured, Color.Error, "Misconfigured")]
    [InlineData(DmarcCheckStatus.MissingAuthorizationRecord, Color.Warning, "Missing authorization")]
    [InlineData(DmarcCheckStatus.NotChecked, Color.Default, "Not checked yet")]
    public void GetColorAndGetLabel_MapEveryStatusToItsExpectedPresentation(DmarcCheckStatus status, Color expectedColor, string expectedLabel)
    {
        Assert.Equal(expectedColor, DmarcStatusPresentation.GetColor(status));
        Assert.Equal(expectedLabel, DmarcStatusPresentation.GetLabel(status));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DmarcStatusPresentationTests`
Expected: FAIL to build — `DmarcStatusPresentation` doesn't exist yet.

- [ ] **Step 3: Create the presentation helper**

Create `src/DotMarc/Reporting/DmarcStatusPresentation.cs`:

```csharp
using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps DmarcCheckStatus to the MudBlazor color/label pair used consistently everywhere
/// it's displayed — Dashboard.razor's DNS Status column and DomainDetail.razor's DMARC record
/// status panel — following the same shared-presentation-logic precedent as DomainStatistics.</summary>
public static class DmarcStatusPresentation
{
    public static Color GetColor(DmarcCheckStatus status) => status switch
    {
        DmarcCheckStatus.Ok => Color.Success,
        DmarcCheckStatus.MissingAuthorizationRecord => Color.Warning,
        DmarcCheckStatus.MissingOwnRecord or DmarcCheckStatus.Misconfigured => Color.Error,
        _ => Color.Default
    };

    public static string GetLabel(DmarcCheckStatus status) => status switch
    {
        DmarcCheckStatus.Ok => "OK",
        DmarcCheckStatus.MissingOwnRecord => "No DMARC record",
        DmarcCheckStatus.Misconfigured => "Misconfigured",
        DmarcCheckStatus.MissingAuthorizationRecord => "Missing authorization",
        _ => "Not checked yet"
    };
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DmarcStatusPresentationTests`
Expected: PASS.

- [ ] **Step 5: Update `Dashboard.razor`**

Change the header row from:

```razor
        <HeaderContent>
            <MudTh>Domain</MudTh>
            <MudTh>Status</MudTh>
            <MudTh>Pass rate (30d)</MudTh>
            <MudTh>Last report</MudTh>
            <MudTh>Monitored</MudTh>
        </HeaderContent>
```

to:

```razor
        <HeaderContent>
            <MudTh>Domain</MudTh>
            <MudTh>Report Status</MudTh>
            <MudTh>Pass rate (30d)</MudTh>
            <MudTh>Last report</MudTh>
            <MudTh>Monitored</MudTh>
            <MudTh>DNS Status</MudTh>
        </HeaderContent>
```

Change the `RowTemplate` from:

```razor
        <RowTemplate>
            <MudTd @onclick="@(() => Navigation.NavigateTo($"/domains/{Uri.EscapeDataString(context.Name)}"))" Style="cursor:pointer">@context.Name</MudTd>
            <MudTd @onclick="@(() => Navigation.NavigateTo($"/domains/{Uri.EscapeDataString(context.Name)}"))" Style="cursor:pointer"><MudChip T="string" Color="@context.StatusColor" Size="Size.Small">@context.Status</MudChip></MudTd>
            <MudTd @onclick="@(() => Navigation.NavigateTo($"/domains/{Uri.EscapeDataString(context.Name)}"))" Style="cursor:pointer">@(context.PassRate is { } rate ? rate.ToString("P1") : "—")</MudTd>
            <MudTd @onclick="@(() => Navigation.NavigateTo($"/domains/{Uri.EscapeDataString(context.Name)}"))" Style="cursor:pointer">@(context.LastReportReceivedUtc is { } last ? last.ToString("g") : "never")</MudTd>
            <MudTd>
                <MudSwitch T="bool" Value="context.IsMonitored" ValueChanged="@(v => ToggleMonitoredAsync(context, v))" Color="Color.Primary" />
            </MudTd>
        </RowTemplate>
```

to:

```razor
        <RowTemplate>
            <MudTd @onclick="@(() => Navigation.NavigateTo($"/domains/{Uri.EscapeDataString(context.Name)}"))" Style="cursor:pointer">@context.Name</MudTd>
            <MudTd @onclick="@(() => Navigation.NavigateTo($"/domains/{Uri.EscapeDataString(context.Name)}"))" Style="cursor:pointer"><MudChip T="string" Color="@context.StatusColor" Size="Size.Small">@context.Status</MudChip></MudTd>
            <MudTd @onclick="@(() => Navigation.NavigateTo($"/domains/{Uri.EscapeDataString(context.Name)}"))" Style="cursor:pointer">@(context.PassRate is { } rate ? rate.ToString("P1") : "—")</MudTd>
            <MudTd @onclick="@(() => Navigation.NavigateTo($"/domains/{Uri.EscapeDataString(context.Name)}"))" Style="cursor:pointer">@(context.LastReportReceivedUtc is { } last ? last.ToString("g") : "never")</MudTd>
            <MudTd>
                <MudSwitch T="bool" Value="context.IsMonitored" ValueChanged="@(v => ToggleMonitoredAsync(context, v))" Color="Color.Primary" />
            </MudTd>
            <MudTd @onclick="@(() => Navigation.NavigateTo($"/domains/{Uri.EscapeDataString(context.Name)}"))" Style="cursor:pointer">
                <MudChip T="string" Color="@DmarcStatusPresentation.GetColor(context.DmarcCheckStatus)" Size="Size.Small">@DmarcStatusPresentation.GetLabel(context.DmarcCheckStatus)</MudChip>
            </MudTd>
        </RowTemplate>
```

Add `@using DotMarc.Reporting` alongside the existing `@using` lines at the top of the file (check
first — `DomainStatistics` already lives in `DotMarc.Reporting` and this file already calls it, so
this using directive most likely already exists; only add it if it doesn't).

Change:

```csharp
                return new DomainRow(d.Id, d.Name, status, color, passRate, d.LastReportReceivedUtc, d.IsMonitored);
```

to:

```csharp
                return new DomainRow(d.Id, d.Name, status, color, passRate, d.LastReportReceivedUtc, d.IsMonitored, d.DmarcCheckStatus);
```

Change:

```csharp
    private sealed record DomainRow(int Id, string Name, string Status, Color StatusColor, double? PassRate, DateTimeOffset? LastReportReceivedUtc, bool IsMonitored);
```

to:

```csharp
    private sealed record DomainRow(int Id, string Name, string Status, Color StatusColor, double? PassRate, DateTimeOffset? LastReportReceivedUtc, bool IsMonitored, DmarcCheckStatus DmarcCheckStatus);
```

- [ ] **Step 6: Build to confirm it compiles**

Run: `dotnet build dotMARC.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 7: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS.

- [ ] **Step 8: Manual verification**

If the environment allows running the app (`docker compose up postgres` plus `dotnet run --project src/DotMarc/DotMarc.csproj` with the Graph/EntraId env vars set, per the README's Development
section) and signing in: confirm the Dashboard's domain table shows "Report Status" and "DNS
Status" as separate columns, and a domain with no DMARC check yet shows "Not checked yet" in a
neutral color. If the environment doesn't allow this (a known, previously-hit limitation — no
local Postgres port available, no interactive Entra sign-in), report clearly in your report which
steps you could and couldn't perform, and why — this is an acceptable, expected limitation, not a
blocker.

- [ ] **Step 9: Commit**

```bash
git add src/DotMarc/Reporting/DmarcStatusPresentation.cs test/DotMarc.Tests/Reporting/DmarcStatusPresentationTests.cs src/DotMarc/Components/Pages/Dashboard.razor
git commit -m "Show DNS status on the Dashboard"
```

---

### Task 6: Domain detail shows full DMARC status

**Files:**
- Modify: `src/DotMarc/Components/Pages/DomainDetail.razor`

**Interfaces:**
- Consumes: `Domain.DmarcCheckStatus`/`DmarcCheckedUtc`/`DmarcCheckDetail` (Task 2),
  `DmarcStatusPresentation.GetColor`/`GetLabel` (Task 5).

No query changes needed: `OnInitializedAsync` already fetches the full `Domain` entity (not a
projection), so the three new fields are already included in `_domain` once Task 2's migration has
run — this task is markup-only. No automated tests, same reasoning as Task 5.

- [ ] **Step 1: Add the panel**

In `src/DotMarc/Components/Pages/DomainDetail.razor`, insert this new `MudPaper` block immediately
after the closing `</MudChart>` tag and before the closing `</MudTabPanel>` of the "Overview" tab:

```razor
            <MudPaper Class="pa-4 mt-4" Elevation="1">
                <MudText Typo="Typo.subtitle1" Class="mb-2">DMARC record status</MudText>
                @if (_domain.DmarcCheckStatus == DmarcCheckStatus.NotChecked)
                {
                    <MudText Typo="Typo.body2">Not checked yet.</MudText>
                }
                else
                {
                    <MudChip T="string" Color="@DmarcStatusPresentation.GetColor(_domain.DmarcCheckStatus)" Size="Size.Small">@DmarcStatusPresentation.GetLabel(_domain.DmarcCheckStatus)</MudChip>
                    @if (_domain.DmarcCheckedUtc is { } checkedUtc)
                    {
                        <MudText Typo="Typo.caption" Class="mt-1">Last checked: @checkedUtc.ToString("yyyy-MM-dd HH:mm:ss")</MudText>
                    }
                    @if (!string.IsNullOrWhiteSpace(_domain.DmarcCheckDetail))
                    {
                        <MudText Typo="Typo.body2" Class="mt-1">@_domain.DmarcCheckDetail</MudText>
                    }
                }
            </MudPaper>
```

Add `@using DotMarc.Reporting` to the file's existing `@using` block if it isn't already there
(check first — it may already be present from the existing `DomainStatistics` usage on this same
page).

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build dotMARC.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 3: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS.

- [ ] **Step 4: Manual verification**

If the environment allows it (same setup as Task 5's manual step): navigate to a domain's detail
page (`/domains/<name>`) and confirm the Overview tab shows the "DMARC record status" panel with
the correct status chip, last-checked time, and detail text (or "Not checked yet." for a domain
with no check yet). If not possible in this environment, report which steps were skipped and why
— expected, not a blocker.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Components/Pages/DomainDetail.razor
git commit -m "Show DMARC record status on the domain detail page"
```
