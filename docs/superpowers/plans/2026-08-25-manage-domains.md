# Manage Domains Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user register a domain for monitoring (and remove or pin/unpin one) before its first DMARC report arrives, so "missing expected report" detection covers a domain whose `rua=` DNS record was never configured correctly — not just one that stopped reporting.

**Architecture:** A pure `DomainNameValidator` (trim/lowercase/format-check) feeds a small `DomainManagementService` static class (add/remove/pin, operating on `DotMarcDbContext`, following the existing `DatabaseMigrator`/`PollingService` "thin adapter over the DbContext" pattern) that a new `/domains` Razor page calls. A `MudDialog` confirms destructive deletes. A single link in the shared `MudAppBar` is the only navigation change — this app currently has no nav menu at all.

**Tech Stack:** ASP.NET Core Blazor Server, MudBlazor 9.8.0, EF Core + Npgsql, xUnit + Testcontainers.PostgreSql (existing test stack — no new test dependency added).

## Global Constraints

- Domain names are normalized to trimmed, lowercase form before storage — `PollingService` matches incoming reports to `Domain` rows by exact-string equality on `Name` (`PollingService.cs:200`), and DMARC XML conventionally reports domains lowercase, so a mismatched case would silently create a duplicate row instead of matching.
- Validation: non-empty after trim, contains at least one `.`, no internal whitespace.
- A domain added via this feature is pinned (`IsPinned = true`) immediately — the entire purpose of adding it here is missing-report monitoring.
- Deleting a domain is allowed regardless of report history, gated behind a confirmation dialog that states the exact report count when it's non-zero (cascade delete already enforced at the DB level — `DotMarcDbContext.cs:28`).
- No general navigation menu is introduced — only one link ("Manage domains") in the existing `MudAppBar`.
- No new NuGet packages. No changes to `PollingService`'s matching logic.

---

### Task 1: `DomainNameValidator`

**Files:**
- Create: `src/DotMarc/Data/DomainNameValidator.cs`
- Test: `test/DotMarc.Tests/Data/DomainNameValidatorTests.cs`

**Interfaces:**
- Produces: `DotMarc.Data.DomainNameValidator.TryNormalize(string input, out string normalized) : bool` — used by Task 2's `DomainManagementService.AddDomainAsync`.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Data/DomainNameValidatorTests.cs`:

```csharp
using DotMarc.Data;
using Xunit;

namespace DotMarc.Tests.Data;

public sealed class DomainNameValidatorTests
{
    [Theory]
    [InlineData("Contoso.com", "contoso.com")]
    [InlineData("  contoso.com  ", "contoso.com")]
    [InlineData("SUB.Contoso.IO", "sub.contoso.io")]
    public void TryNormalize_TrimsAndLowercases_ValidInput(string input, string expected)
    {
        var result = DomainNameValidator.TryNormalize(input, out var normalized);

        Assert.True(result);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nodothere")]
    [InlineData("has space.com")]
    [InlineData("contoso .com")]
    public void TryNormalize_RejectsInvalidInput(string input)
    {
        var result = DomainNameValidator.TryNormalize(input, out _);

        Assert.False(result);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DomainNameValidatorTests`
Expected: FAIL to build — `DomainNameValidator` does not exist yet.

- [ ] **Step 3: Write the implementation**

Create `src/DotMarc/Data/DomainNameValidator.cs`:

```csharp
namespace DotMarc.Data;

/// <summary>Pure validation/normalization for a user-supplied domain name, used when a domain is
/// added for monitoring before any report has arrived for it (see DomainManagementService).
/// Lowercasing here is not cosmetic: PollingService matches an incoming report's domain to a
/// Domain row by exact string equality on Name (PollingService.cs:200), and DMARC aggregate report
/// XML conventionally reports the domain in lowercase — a mixed-case Name stored here would
/// silently fail to match its first real report and produce a duplicate row instead.</summary>
public static class DomainNameValidator
{
    public static bool TryNormalize(string input, out string normalized)
    {
        normalized = input.Trim().ToLowerInvariant();
        return normalized.Length > 0 && normalized.Contains('.') && !normalized.Any(char.IsWhiteSpace);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DomainNameValidatorTests`
Expected: PASS (8 tests: 3 theory cases + 5 theory cases).

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Data/DomainNameValidator.cs test/DotMarc.Tests/Data/DomainNameValidatorTests.cs
git commit -m "Add DomainNameValidator for normalizing manually-added domain names"
```

---

### Task 2: `DomainManagementService.AddDomainAsync`

**Files:**
- Create: `src/DotMarc/Data/DomainManagementService.cs`
- Test: `test/DotMarc.Tests/Data/DomainManagementServiceTests.cs`
- Modify: `test/DotMarc.Tests/Ingestion/PollingServiceTests.cs` (add one regression test)

**Interfaces:**
- Consumes: `DomainNameValidator.TryNormalize` (Task 1); `DotMarcDbContext.Domains` (`src/DotMarc/Data/DotMarcDbContext.cs`); `Domain` entity (`src/DotMarc/Data/Domain.cs`).
- Produces: `DotMarc.Data.DomainManagementService.AddDomainResult` enum (`Added`, `InvalidName`, `AlreadyMonitored`) and `DomainManagementService.AddDomainAsync(DotMarcDbContext context, string rawName, CancellationToken cancellationToken) : Task<AddDomainResult>` — used by Task 4's `ManageDomains.razor`.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Data/DomainManagementServiceTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class DomainManagementServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DomainManagementServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private DotMarcDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DotMarcDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new DotMarcDbContext(options);
    }

    [Fact]
    public async Task AddDomainAsync_CreatesAPinnedDomain_WithNormalizedName()
    {
        using var context = CreateContext();

        var result = await DomainManagementService.AddDomainAsync(context, "Contoso.COM", CancellationToken.None);

        Assert.Equal(DomainManagementService.AddDomainResult.Added, result);
        var domain = context.Domains.Single();
        Assert.Equal("contoso.com", domain.Name);
        Assert.True(domain.IsPinned);
        Assert.Null(domain.LastReportReceivedUtc);
    }

    [Fact]
    public async Task AddDomainAsync_RejectsInvalidName()
    {
        using var context = CreateContext();

        var result = await DomainManagementService.AddDomainAsync(context, "not-a-domain", CancellationToken.None);

        Assert.Equal(DomainManagementService.AddDomainResult.InvalidName, result);
        Assert.Empty(context.Domains);
    }

    [Fact]
    public async Task AddDomainAsync_RejectsDuplicate_RegardlessOfCasing()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, "contoso.com", CancellationToken.None);

        var result = await DomainManagementService.AddDomainAsync(context, "CONTOSO.com", CancellationToken.None);

        Assert.Equal(DomainManagementService.AddDomainResult.AlreadyMonitored, result);
        Assert.Single(context.Domains);
    }
}
```

Then add this regression test to the existing `test/DotMarc.Tests/Ingestion/PollingServiceTests.cs` (inside the `PollingServiceTests` class, alongside the other `[Fact]` methods — the file already has `using DotMarc.Data;` at the top):

```csharp
    [Fact]
    public async Task PollOnceAsync_MatchesAManuallyAddedDomain_InsteadOfCreatingADuplicate()
    {
        // Regression coverage for the "manage domains" feature: a domain added up front (before
        // any report has arrived for it) must be picked up by its Name when the first real report
        // lands, not treated as unseen and duplicated.
        using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, "contoso.io", CancellationToken.None);
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
        var domain = verify.Domains.Include(d => d.Reports).Single();
        Assert.Equal("contoso.io", domain.Name);
        Assert.True(domain.IsPinned);
        Assert.Single(domain.Reports);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "DomainManagementServiceTests|PollOnceAsync_MatchesAManuallyAddedDomain_InsteadOfCreatingADuplicate"`
Expected: FAIL to build — `DomainManagementService` does not exist yet.

- [ ] **Step 3: Write the implementation**

Create `src/DotMarc/Data/DomainManagementService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Data;

/// <summary>Add/remove/pin operations for Domain rows created through the "Manage domains" page,
/// as opposed to auto-discovery from an incoming report (see PollingService.StoreReportAsync).
/// Follows this project's DatabaseMigrator/PollingService convention of a static class operating
/// directly on a caller-supplied DotMarcDbContext, rather than owning its own context lifetime.</summary>
public static class DomainManagementService
{
    public enum AddDomainResult { Added, InvalidName, AlreadyMonitored }

    /// <summary>Creates a pinned Domain row with no reports yet, so it immediately shows as
    /// "Missing" on the Dashboard (Dashboard.razor's existing IsPinned &amp;&amp;
    /// LastReportReceivedUtc-is-null check) until its first real report arrives.</summary>
    public static async Task<AddDomainResult> AddDomainAsync(DotMarcDbContext context, string rawName, CancellationToken cancellationToken = default)
    {
        if (!DomainNameValidator.TryNormalize(rawName, out var normalized))
        {
            return AddDomainResult.InvalidName;
        }

        var exists = await context.Domains.AnyAsync(d => d.Name == normalized, cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return AddDomainResult.AlreadyMonitored;
        }

        context.Domains.Add(new Domain { Name = normalized, FirstSeenUtc = DateTimeOffset.UtcNow, IsPinned = true });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The unique index on Domain.Name (DotMarcDbContext.cs) caught a race: another request
            // inserted the same domain between our AnyAsync check and this save. Same outcome as
            // the pre-check catching it, just reported the same way to the caller.
            return AddDomainResult.AlreadyMonitored;
        }

        return AddDomainResult.Added;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "DomainManagementServiceTests|PollOnceAsync_MatchesAManuallyAddedDomain_InsteadOfCreatingADuplicate"`
Expected: PASS (3 new tests in `DomainManagementServiceTests`, 1 new test in `PollingServiceTests`).

- [ ] **Step 5: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS (all existing tests plus the new ones).

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Data/DomainManagementService.cs test/DotMarc.Tests/Data/DomainManagementServiceTests.cs test/DotMarc.Tests/Ingestion/PollingServiceTests.cs
git commit -m "Add DomainManagementService.AddDomainAsync, with PollingService interop regression test"
```

---

### Task 3: `DomainManagementService.RemoveDomainAsync` and `SetPinnedAsync`

**Files:**
- Modify: `src/DotMarc/Data/DomainManagementService.cs`
- Modify: `test/DotMarc.Tests/Data/DomainManagementServiceTests.cs`

**Interfaces:**
- Consumes: `Report`, `ReportRecord`, `DispositionResult`, `AuthResult` entities (`src/DotMarc/Data/*.cs`).
- Produces: `DomainManagementService.RemoveDomainAsync(DotMarcDbContext context, int domainId, CancellationToken cancellationToken) : Task` and `DomainManagementService.SetPinnedAsync(DotMarcDbContext context, int domainId, bool isPinned, CancellationToken cancellationToken) : Task` — used by Task 4's `ManageDomains.razor`.

- [ ] **Step 1: Write the failing tests**

Add to `test/DotMarc.Tests/Data/DomainManagementServiceTests.cs` (inside the existing class):

```csharp
    [Fact]
    public async Task RemoveDomainAsync_DeletesDomainWithNoReports()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, "contoso.com", CancellationToken.None);
        var domainId = context.Domains.Single().Id;

        await DomainManagementService.RemoveDomainAsync(context, domainId, CancellationToken.None);

        Assert.Empty(context.Domains);
    }

    [Fact]
    public async Task RemoveDomainAsync_CascadesReportsAndRecords()
    {
        using var context = CreateContext();
        var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow };
        var report = new Report
        {
            Domain = domain,
            ReportingOrg = "google.com",
            ReportId = "1",
            DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
            DateRangeEndUtc = DateTimeOffset.UtcNow,
            RawXml = "<feedback/>",
            ReceivedUtc = DateTimeOffset.UtcNow
        };
        report.Records.Add(new ReportRecord
        {
            SourceIp = "198.51.100.7",
            MessageCount = 5,
            Disposition = DispositionResult.None,
            SpfResult = AuthResult.Pass,
            DkimResult = AuthResult.Pass,
            HeaderFrom = "contoso.io"
        });
        context.Domains.Add(domain);
        context.Reports.Add(report);
        await context.SaveChangesAsync();

        await DomainManagementService.RemoveDomainAsync(context, domain.Id, CancellationToken.None);

        using var verify = CreateContext();
        Assert.Empty(verify.Domains);
        Assert.Empty(verify.Reports);
        Assert.Empty(verify.ReportRecords);
    }

    [Fact]
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

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "RemoveDomainAsync_DeletesDomainWithNoReports|RemoveDomainAsync_CascadesReportsAndRecords|SetPinnedAsync_TogglesIsPinned"`
Expected: FAIL to build — `RemoveDomainAsync`/`SetPinnedAsync` don't exist yet.

- [ ] **Step 3: Write the implementation**

Add to `src/DotMarc/Data/DomainManagementService.cs`, inside the `DomainManagementService` class, after `AddDomainAsync`:

```csharp
    /// <summary>Permanently deletes a Domain row. DotMarcDbContext.cs configures cascade delete
    /// from Domain to Report and Report to ReportRecord, so this also removes all report history
    /// for the domain — callers (ManageDomains.razor) confirm that with the user first when the
    /// domain has any reports.</summary>
    public static async Task RemoveDomainAsync(DotMarcDbContext context, int domainId, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        context.Domains.Remove(domain);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task SetPinnedAsync(DotMarcDbContext context, int domainId, bool isPinned, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        domain.IsPinned = isPinned;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "RemoveDomainAsync_DeletesDomainWithNoReports|RemoveDomainAsync_CascadesReportsAndRecords|SetPinnedAsync_TogglesIsPinned"`
Expected: PASS.

- [ ] **Step 5: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Data/DomainManagementService.cs test/DotMarc.Tests/Data/DomainManagementServiceTests.cs
git commit -m "Add DomainManagementService.RemoveDomainAsync and SetPinnedAsync"
```

---

### Task 4: `ManageDomains.razor` page and delete-confirmation dialog

**Files:**
- Create: `src/DotMarc/Components/Pages/ManageDomains.razor`
- Create: `src/DotMarc/Components/Dialogs/ConfirmDeleteDomainDialog.razor`

**Interfaces:**
- Consumes: `DomainManagementService.AddDomainAsync/RemoveDomainAsync/SetPinnedAsync` (Tasks 2–3); `IDbContextFactory<DotMarcDbContext>` (already registered in `Program.cs:48`, same injection pattern as `Dashboard.razor`/`DomainDetail.razor`); MudBlazor's `IDialogService`/`DialogParameters<T>`/`IMudDialogInstance` (already available — `MudDialogProvider` is registered in `MainLayout.razor`).
- Produces: route `/domains`, consumed by Task 5's app-bar link.

This task has no automated tests: the existing test suite (see `test/DotMarc.Tests/`) covers data/service logic only — there's no Blazor component-rendering test dependency (bUnit) anywhere in this project, and `Dashboard.razor`/`DomainDetail.razor`/`ParseFailures.razor` follow the same pattern of testing only the logic they call, not the markup itself. Verification here is a build check plus a manual run-through.

- [ ] **Step 1: Create the confirmation dialog**

Create `src/DotMarc/Components/Dialogs/ConfirmDeleteDomainDialog.razor`:

```razor
<MudDialog>
    <TitleContent>Remove domain</TitleContent>
    <DialogContent>
        @if (ReportCount > 0)
        {
            <MudText Color="Color.Error">
                This will permanently delete <b>@DomainName</b> and all @ReportCount report(s) received for it. This cannot be undone.
            </MudText>
        }
        else
        {
            <MudText>Remove <b>@DomainName</b> from monitoring? It has no report history.</MudText>
        }
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        <MudButton Color="Color.Error" Variant="Variant.Filled" OnClick="Confirm">Remove</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public string DomainName { get; set; } = "";
    [Parameter] public int ReportCount { get; set; }

    private void Confirm() => MudDialog.Close(DialogResult.Ok(true));
    private void Cancel() => MudDialog.Cancel();
}
```

- [ ] **Step 2: Create the page**

Create `src/DotMarc/Components/Pages/ManageDomains.razor`:

```razor
@page "/domains"
@using DotMarc.Data
@using DotMarc.Components.Dialogs
@using Microsoft.EntityFrameworkCore
@inject IDbContextFactory<DotMarcDbContext> DbFactory
@inject IDialogService DialogService
@inject ISnackbar Snackbar

<MudButton Href="/dashboard" StartIcon="@Icons.Material.Filled.ArrowBack" Class="mb-2">Back</MudButton>
<MudText Typo="Typo.h4" Class="mb-4">Manage domains</MudText>

<MudPaper Class="pa-4 mb-4" Elevation="1">
    <MudGrid>
        <MudItem xs="9">
            <MudTextField @bind-Value="_newDomainName" Label="Domain name" Placeholder="contoso.com"
                          Error="@(_addError is not null)" ErrorText="@_addError" Immediate="true" />
        </MudItem>
        <MudItem xs="3" Class="d-flex align-center">
            <MudButton Variant="Variant.Filled" Color="Color.Primary" StartIcon="@Icons.Material.Filled.Add"
                       OnClick="AddDomainAsync">Add domain</MudButton>
        </MudItem>
    </MudGrid>
</MudPaper>

@if (_domains is null)
{
    <MudProgressCircular Indeterminate="true" />
}
else
{
    <MudTable Items="_domains" Hover="true" T="DomainRow">
        <HeaderContent>
            <MudTh>Domain</MudTh>
            <MudTh>Pinned</MudTh>
            <MudTh>Reports</MudTh>
            <MudTh>Last report</MudTh>
            <MudTh></MudTh>
        </HeaderContent>
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
    </MudTable>
}

@code {
    private List<DomainRow>? _domains;
    private string _newDomainName = "";
    private string? _addError;

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        _domains = await db.Domains
            .AsNoTracking()
            .OrderBy(d => d.Name)
            .Select(d => new DomainRow(d.Id, d.Name, d.IsPinned, d.Reports.Count, d.LastReportReceivedUtc))
            .ToListAsync();
    }

    private async Task AddDomainAsync()
    {
        _addError = null;
        await using var db = await DbFactory.CreateDbContextAsync();
        var result = await DomainManagementService.AddDomainAsync(db, _newDomainName, CancellationToken.None);

        _addError = result switch
        {
            DomainManagementService.AddDomainResult.InvalidName => "Enter a valid domain name (e.g. contoso.com).",
            DomainManagementService.AddDomainResult.AlreadyMonitored => "That domain is already monitored.",
            _ => null
        };

        if (result == DomainManagementService.AddDomainResult.Added)
        {
            _newDomainName = "";
            await LoadAsync();
        }
    }

    private async Task SetPinnedAsync(DomainRow row, bool isPinned)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        await DomainManagementService.SetPinnedAsync(db, row.Id, isPinned, CancellationToken.None);
        await LoadAsync();
    }

    private async Task DeleteDomainAsync(DomainRow row)
    {
        var parameters = new DialogParameters<ConfirmDeleteDomainDialog>
        {
            { x => x.DomainName, row.Name },
            { x => x.ReportCount, row.ReportCount }
        };
        var dialogRef = await DialogService.ShowAsync<ConfirmDeleteDomainDialog>("Remove domain", parameters);
        var result = await dialogRef.Result;

        if (result is { Canceled: false })
        {
            try
            {
                await using var db = await DbFactory.CreateDbContextAsync();
                await DomainManagementService.RemoveDomainAsync(db, row.Id, CancellationToken.None);
                await LoadAsync();
            }
            catch (Exception)
            {
                // Leave the row in place — don't optimistically remove it from _domains before
                // the delete has actually succeeded.
                Snackbar.Add($"Failed to remove {row.Name}. Try again.", Severity.Error);
            }
        }
    }

    private sealed record DomainRow(int Id, string Name, bool IsPinned, int ReportCount, DateTimeOffset? LastReportReceivedUtc);
}
```

- [ ] **Step 3: Build to confirm it compiles**

Run: `dotnet build dotMARC.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 4: Manual verification**

Run: `docker compose up postgres` (in one terminal), then `dotnet run --project src/DotMarc/DotMarc.csproj` (in another, with the Graph/EntraId env vars set per the README's Development section). Sign in, navigate to `https://localhost:<port>/domains` directly (no nav link exists until Task 5), and confirm:
- Adding a well-formed domain (e.g. `Test.Example.com`) succeeds, clears the field, and the table shows it as `test.example.com`, pinned, 0 reports, "never".
- Navigating to `/dashboard` shows that domain with status "Missing" (red).
- Re-adding the same domain (any casing) shows the inline "already monitored" error and does not add a second row.
- Adding `notadomain` (no dot) shows the inline "Enter a valid domain name" error.
- Toggling the pin switch off, then reloading the page, shows it unpinned (and the Dashboard no longer shows it as "Missing").
- Clicking delete on a domain with 0 reports shows the plain confirmation; confirming removes it from the table.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Components/Pages/ManageDomains.razor src/DotMarc/Components/Dialogs/ConfirmDeleteDomainDialog.razor
git commit -m "Add /domains page for registering, pinning, and removing monitored domains"
```

---

### Task 5: App-bar navigation link

**Files:**
- Modify: `src/DotMarc/Components/Layout/MainLayout.razor`

**Interfaces:**
- Consumes: route `/domains` (Task 4).

- [ ] **Step 1: Add the link**

In `src/DotMarc/Components/Layout/MainLayout.razor`, replace:

```razor
    <MudAppBar Elevation="1">
        <MudText Typo="Typo.h6">dotMARC</MudText>
    </MudAppBar>
```

with:

```razor
    <MudAppBar Elevation="1">
        <MudText Typo="Typo.h6">dotMARC</MudText>
        <MudSpacer />
        <MudButton Href="/domains" Color="Color.Inherit">Manage domains</MudButton>
    </MudAppBar>
```

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build dotMARC.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 3: Manual verification**

With the app still running from Task 4's manual check (or restarted), confirm the "Manage domains" link is visible in the app bar on every page (`/dashboard`, `/domains`, `/parse-failures`, a `/domains/{name}` detail page) and clicking it from any of them navigates to `/domains`.

- [ ] **Step 4: Commit**

```bash
git add src/DotMarc/Components/Layout/MainLayout.razor
git commit -m "Add Manage domains link to the app bar"
```
