# Poll Status Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist per-poll-cycle outcomes (last polled time, message/report counts, success/error) so the Dashboard can show whether `PollingService` is actually working, and show that most recent status in a new panel below the domain table.

**Architecture:** Two new EF Core entities (`PollCycle` raw history, `PollCycleDailySummary` permanent daily rollup). `PollingService`'s existing per-message loop starts counting instead of discarding the counts; `RunPollCycleAsync` writes one `PollCycle` row per cycle it actually runs (success or failure), then an inline prune-and-rollup step folds any row more than 7 days old into its day's summary and deletes it. `Dashboard.razor` reads the single latest `PollCycle` row the same way it already reads everything else — a fresh `IDbContextFactory`-created context per load.

**Tech Stack:** ASP.NET Core Blazor Server, MudBlazor 9.8.0, EF Core + Npgsql, xUnit + Testcontainers.PostgreSql (existing stack — no new dependency).

## Global Constraints

- "Last polled" timestamp is formatted with .NET's `"O"` (round-trip) format specifier — a valid ISO 8601 timestamp.
- Raw `PollCycle` history is kept 7 days, anchored to UTC calendar-day boundaries (a day is only rolled up once it's fully closed out — no partial-day merge across multiple prune passes).
- `PollCycleDailySummary` rows are kept indefinitely (small — one row per day, not per cycle).
- A poll cycle skipped because another replica holds the leader lock writes no `PollCycle` row.
- No UI history/trend view is built — the Dashboard shows only the single latest `PollCycle` row. `PollCycleDailySummary` data isn't surfaced anywhere yet.
- No alerting (email/webhook) on a failed or overdue cycle.
- No change to `ParseFailure`'s own per-message retry/dedup behavior — this only adds cycle-level counting around the existing logic.

---

### Task 1: `PollCycle` and `PollCycleDailySummary` entities

**Files:**
- Create: `src/DotMarc/Data/PollCycle.cs`
- Create: `src/DotMarc/Data/PollCycleDailySummary.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs`
- Modify: `test/DotMarc.Tests/Data/DotMarcDbContextTests.cs`
- (generated) `src/DotMarc/Migrations/` — a new EF Core migration

**Interfaces:**
- Produces: `DotMarc.Data.PollCycle` (`Id`, `PolledUtc`, `MessagesChecked`, `ReportsParsed`, `ParseFailures`, `Succeeded`, `ErrorMessage`) and `DotMarc.Data.PollCycleDailySummary` (`Id`, `Date`, `TotalCycles`, `SuccessfulCycles`, `FailedCycles`, `TotalMessagesChecked`, `TotalReportsParsed`, `TotalParseFailures`), plus `DotMarcDbContext.PollCycles`/`PollCycleDailySummaries` — used by Task 2 (`PollingService`) and Task 4 (`Dashboard.razor`).

- [ ] **Step 1: Write the failing tests**

Add to `test/DotMarc.Tests/Data/DotMarcDbContextTests.cs`, inside the existing `DotMarcDbContextTests` class:

```csharp
    [Fact]
    public void CanInsertAndQuery_PollCycle()
    {
        using (var context = CreateContext())
        {
            context.PollCycles.Add(new PollCycle
            {
                PolledUtc = DateTimeOffset.UtcNow,
                MessagesChecked = 4,
                ReportsParsed = 3,
                ParseFailures = 1,
                Succeeded = true
            });
            context.SaveChanges();
        }

        using (var verify = CreateContext())
        {
            var pollCycle = verify.PollCycles.Single();
            Assert.Equal(4, pollCycle.MessagesChecked);
            Assert.Equal(3, pollCycle.ReportsParsed);
            Assert.Equal(1, pollCycle.ParseFailures);
            Assert.True(pollCycle.Succeeded);
            Assert.Null(pollCycle.ErrorMessage);
        }
    }

    [Fact]
    public void PollCycleDailySummary_DateMustBeUnique()
    {
        using var context = CreateContext();
        context.PollCycleDailySummaries.Add(new PollCycleDailySummary { Date = new DateOnly(2026, 8, 1) });
        context.SaveChanges();

        context.PollCycleDailySummaries.Add(new PollCycleDailySummary { Date = new DateOnly(2026, 8, 1) });

        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "CanInsertAndQuery_PollCycle|PollCycleDailySummary_DateMustBeUnique"`
Expected: FAIL to build — `PollCycle`/`PollCycleDailySummary`/`DotMarcDbContext.PollCycles`/`PollCycleDailySummaries` don't exist yet.

- [ ] **Step 3: Create the entities**

Create `src/DotMarc/Data/PollCycle.cs`:

```csharp
namespace DotMarc.Data;

/// <summary>One row per poll cycle that actually ran (a cycle skipped because another replica held
/// the leader lock — see PollingService.RunPollCycleAsync — writes nothing here; "last polled"
/// should reflect when polling actually happened, not a skip). Raw rows are kept for 7 days, then
/// rolled up into PollCycleDailySummary and deleted (see PollingService.RollUpStalePollCyclesAsync).</summary>
public sealed class PollCycle
{
    public int Id { get; set; }
    public DateTimeOffset PolledUtc { get; set; }
    public int MessagesChecked { get; set; }
    public int ReportsParsed { get; set; }
    public int ParseFailures { get; set; }
    public bool Succeeded { get; set; }
    public string? ErrorMessage { get; set; }
}
```

Create `src/DotMarc/Data/PollCycleDailySummary.cs`:

```csharp
namespace DotMarc.Data;

/// <summary>One row per UTC calendar day, created/updated only once that day's raw PollCycle rows
/// are more than 7 days old and get rolled up (see PollingService.RollUpStalePollCyclesAsync). Kept
/// indefinitely — small compared to the raw rows it replaces (one row per day instead of one row
/// per poll cycle).</summary>
public sealed class PollCycleDailySummary
{
    public int Id { get; set; }
    public required DateOnly Date { get; set; }
    public int TotalCycles { get; set; }
    public int SuccessfulCycles { get; set; }
    public int FailedCycles { get; set; }
    public int TotalMessagesChecked { get; set; }
    public int TotalReportsParsed { get; set; }
    public int TotalParseFailures { get; set; }
}
```

- [ ] **Step 4: Register the entities in `DotMarcDbContext`**

In `src/DotMarc/Data/DotMarcDbContext.cs`, add two `DbSet` properties alongside the existing ones:

```csharp
    public DbSet<PollCycle> PollCycles => Set<PollCycle>();
    public DbSet<PollCycleDailySummary> PollCycleDailySummaries => Set<PollCycleDailySummary>();
```

And add this configuration inside `OnModelCreating`, alongside the existing `modelBuilder.Entity<...>` blocks:

```csharp
        modelBuilder.Entity<PollCycle>(entity =>
        {
            entity.HasIndex(p => p.PolledUtc);
        });

        modelBuilder.Entity<PollCycleDailySummary>(entity =>
        {
            entity.HasIndex(d => d.Date).IsUnique();
        });
```

- [ ] **Step 5: Generate the EF Core migration**

Run: `dotnet ef migrations add AddPollCycleTracking --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj`
Expected: creates three new files under `src/DotMarc/Migrations/` (a `..._AddPollCycleTracking.cs`, its `.Designer.cs`, and an updated `DotMarcDbContextModelSnapshot.cs`). This uses the project's already-configured local `dotnet-ef` tool (`.config/dotnet-tools.json`) — no separate install step needed. The command builds the project and inspects the real model, so review the generated migration's `Up`/`Down` methods to confirm they create exactly the `PollCycles` and `PollCycleDailySummaries` tables with the indexes from Step 4 — do not hand-edit the generated files unless something is actually wrong.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "CanInsertAndQuery_PollCycle|PollCycleDailySummary_DateMustBeUnique"`
Expected: PASS. (These tests call `context.Database.MigrateAsync()` in `InitializeAsync`, so the new migration from Step 5 is what makes the tables exist.)

- [ ] **Step 7: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS (all existing tests plus the two new ones).

- [ ] **Step 8: Commit**

```bash
git add src/DotMarc/Data/PollCycle.cs src/DotMarc/Data/PollCycleDailySummary.cs src/DotMarc/Data/DotMarcDbContext.cs src/DotMarc/Migrations/ test/DotMarc.Tests/Data/DotMarcDbContextTests.cs
git commit -m "Add PollCycle and PollCycleDailySummary entities"
```

---

### Task 2: Count and record each poll cycle in `PollingService`

**Files:**
- Modify: `src/DotMarc/Ingestion/PollingService.cs`
- Modify: `test/DotMarc.Tests/Internal/FakeGraphMailboxClient.cs`
- Modify: `test/DotMarc.Tests/Ingestion/PollingServiceLeaderLockTests.cs`

**Interfaces:**
- Consumes: `PollCycle` entity and `DotMarcDbContext.PollCycles` (Task 1).
- Produces: a `PollCycle` row written by `RunPollCycleAsync` after every cycle it actually executes (not one it skipped due to the leader lock) — used by Task 3 (rollup, reading the same table) and Task 4 (`Dashboard.razor`, reading the latest row).

- [ ] **Step 1: Write the failing tests**

Add a failure-simulation flag to `test/DotMarc.Tests/Internal/FakeGraphMailboxClient.cs` — change:

```csharp
    public Task<IReadOnlyList<MailboxMessage>> GetUnreadMessagesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MailboxMessage>>(UnreadMessages);
```

to:

```csharp
    public bool FailGetUnreadMessages { get; set; }

    public Task<IReadOnlyList<MailboxMessage>> GetUnreadMessagesAsync(CancellationToken cancellationToken)
    {
        if (FailGetUnreadMessages)
        {
            throw new HttpRequestException("Simulated Graph failure fetching unread messages.");
        }

        return Task.FromResult<IReadOnlyList<MailboxMessage>>(UnreadMessages);
    }
```

Then, in `test/DotMarc.Tests/Ingestion/PollingServiceLeaderLockTests.cs`:

Extend the existing `RunPollCycleAsync_SkipsPolling_WhenAnotherInstanceHoldsTheLeaderLock` test — inside its `using (var verify = CreateContext())` block, add one line after the existing `Assert.Empty(verify.Reports);`:

```csharp
            Assert.Empty(verify.PollCycles);
```

Extend the existing `RunPollCycleAsync_ProcessesMessages_WhenTheLeaderLockIsFree` test — replace its body's verify block:

```csharp
        using (var verify = CreateContext())
        {
            Assert.Single(verify.Reports);
        }
        Assert.Contains("msg-1", graphClient.MarkedAsRead);
```

with:

```csharp
        using (var verify = CreateContext())
        {
            Assert.Single(verify.Reports);
            var pollCycle = verify.PollCycles.Single();
            Assert.True(pollCycle.Succeeded);
            Assert.Equal(1, pollCycle.MessagesChecked);
            Assert.Equal(1, pollCycle.ReportsParsed);
            Assert.Equal(0, pollCycle.ParseFailures);
            Assert.Null(pollCycle.ErrorMessage);
        }
        Assert.Contains("msg-1", graphClient.MarkedAsRead);
```

Add two new `[Fact]` methods to the same class:

```csharp
    [Fact]
    public async Task RunPollCycleAsync_CountsReportsParsedAndParseFailuresSeparately()
    {
        var graphClient = GraphClientWithOneValidReport();
        graphClient.UnreadMessages.Add(new DotMarc.Graph.MailboxMessage("msg-bad", "Not a report", true));
        graphClient.Attachments["msg-bad"] = [new DotMarc.Graph.MailboxAttachment("garbage.xml", "text/xml", "not xml"u8.ToArray())];

        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
            await service.RunPollCycleAsync(graphClient, context, CancellationToken.None);
        }

        using var verify = CreateContext();
        var pollCycle = verify.PollCycles.Single();
        Assert.Equal(2, pollCycle.MessagesChecked);
        Assert.Equal(1, pollCycle.ReportsParsed);
        Assert.Equal(1, pollCycle.ParseFailures);
        Assert.True(pollCycle.Succeeded);
    }

    [Fact]
    public async Task RunPollCycleAsync_RecordsAFailedCycle_WhenTheMailboxFetchThrows()
    {
        var graphClient = new FakeGraphMailboxClient { FailGetUnreadMessages = true };

        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
            await Assert.ThrowsAsync<HttpRequestException>(() => service.RunPollCycleAsync(graphClient, context, CancellationToken.None));
        }

        using (var verify = CreateContext())
        {
            var pollCycle = verify.PollCycles.Single();
            Assert.False(pollCycle.Succeeded);
            Assert.Contains("Simulated Graph failure", pollCycle.ErrorMessage);
            Assert.Equal(0, pollCycle.MessagesChecked);
            Assert.Equal(0, pollCycle.ReportsParsed);
            Assert.Equal(0, pollCycle.ParseFailures);
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter PollingServiceLeaderLockTests`
Expected: FAIL — `verify.PollCycles` is empty/missing where the new assertions expect rows (the production code doesn't write them yet).

- [ ] **Step 3: Write the implementation**

In `src/DotMarc/Ingestion/PollingService.cs`, add this private record near the top of the class (right after the `PollingLeaderLockKey` constant and field declarations, before the constructors):

```csharp
    private sealed record PollCycleCounts(int MessagesChecked, int ReportsParsed, int ParseFailures);
```

Change the private `PollOnceAsync` method from:

```csharp
    private async Task PollOnceAsync(IGraphMailboxClient graphClient, DotMarcDbContext context, CancellationToken cancellationToken)
    {
        var messages = await graphClient.GetUnreadMessagesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var message in messages)
        {
            if (!message.HasAttachments)
            {
                continue;
            }

            try
            {
                await ProcessMessageAsync(graphClient, context, message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse message {MessageId}; leaving unread for retry.", message.Id);

                // A prior SaveChangesAsync failure earlier in this method (e.g. inside
                // StoreReportAsync) can leave half-built Domain/Report/ReportRecord entities
                // tracked as Added on this shared context. Without clearing the tracker first, the
                // save below would re-attempt those leftover entities alongside the ParseFailure
                // and could throw again here, uncaught, aborting the rest of the poll cycle.
                context.ChangeTracker.Clear();
                await RecordParseFailureAsync(context, message.Id, ex.Message, cancellationToken).ConfigureAwait(false);
            }
        }
    }
```

to:

```csharp
    private async Task<PollCycleCounts> PollOnceAsync(IGraphMailboxClient graphClient, DotMarcDbContext context, CancellationToken cancellationToken)
    {
        var messages = await graphClient.GetUnreadMessagesAsync(cancellationToken).ConfigureAwait(false);

        var reportsParsed = 0;
        var parseFailures = 0;

        foreach (var message in messages)
        {
            if (!message.HasAttachments)
            {
                continue;
            }

            try
            {
                await ProcessMessageAsync(graphClient, context, message, cancellationToken).ConfigureAwait(false);
                reportsParsed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse message {MessageId}; leaving unread for retry.", message.Id);

                // A prior SaveChangesAsync failure earlier in this method (e.g. inside
                // StoreReportAsync) can leave half-built Domain/Report/ReportRecord entities
                // tracked as Added on this shared context. Without clearing the tracker first, the
                // save below would re-attempt those leftover entities alongside the ParseFailure
                // and could throw again here, uncaught, aborting the rest of the poll cycle.
                context.ChangeTracker.Clear();
                await RecordParseFailureAsync(context, message.Id, ex.Message, cancellationToken).ConfigureAwait(false);
                parseFailures++;
            }
        }

        return new PollCycleCounts(messages.Count, reportsParsed, parseFailures);
    }
```

(The public one-argument `PollOnceAsync(CancellationToken)` wrapper and its callers in `PollingServiceTests.cs`/`PollingServiceDiActivationTests.cs` need no change — it's declared to return plain `Task`, and `Task<PollCycleCounts>` is a `Task`, so forwarding the now-`Task<PollCycleCounts>`-returning private call still compiles unchanged.)

Change `RunPollCycleAsync` from:

```csharp
        try
        {
            await PollOnceAsync(graphClient, context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await lockTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
```

to:

```csharp
        try
        {
            PollCycleCounts counts;
            try
            {
                counts = await PollOnceAsync(graphClient, context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                context.ChangeTracker.Clear();
                await RecordPollCycleAsync(context, new PollCycleCounts(0, 0, 0), succeeded: false, ex.Message, cancellationToken).ConfigureAwait(false);
                throw;
            }

            await RecordPollCycleAsync(context, counts, succeeded: true, errorMessage: null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await lockTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
```

Add this new private method after `RunPollCycleAsync` (before the private `PollOnceAsync` method):

```csharp
    /// <summary>Writes one PollCycle row for a cycle that actually ran (never for one skipped due
    /// to the leader lock — see RunPollCycleAsync). Rollup of stale rows happens here too, inline,
    /// rather than as a separate scheduled job — see RollUpStalePollCyclesAsync.</summary>
    private static async Task RecordPollCycleAsync(DotMarcDbContext context, PollCycleCounts counts, bool succeeded, string? errorMessage, CancellationToken cancellationToken)
    {
        context.PollCycles.Add(new PollCycle
        {
            PolledUtc = DateTimeOffset.UtcNow,
            MessagesChecked = counts.MessagesChecked,
            ReportsParsed = counts.ReportsParsed,
            ParseFailures = counts.ParseFailures,
            Succeeded = succeeded,
            ErrorMessage = errorMessage
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter PollingServiceLeaderLockTests`
Expected: PASS (all 4 tests in the file: the 2 extended ones and the 2 new ones).

- [ ] **Step 5: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Ingestion/PollingService.cs test/DotMarc.Tests/Internal/FakeGraphMailboxClient.cs test/DotMarc.Tests/Ingestion/PollingServiceLeaderLockTests.cs
git commit -m "Record poll cycle outcomes (counts, success/failure) as PollCycle rows"
```

---

### Task 3: Roll up and prune stale `PollCycle` rows

**Files:**
- Modify: `src/DotMarc/Ingestion/PollingService.cs`
- Create: `test/DotMarc.Tests/Ingestion/PollCycleRollupTests.cs`

**Interfaces:**
- Consumes: `PollCycle`, `PollCycleDailySummary` entities (Task 1).
- Produces: `PollingService.RollUpStalePollCyclesAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) : Task` — `internal static` so tests can call it directly with hand-seeded, backdated rows (the production caller, `RecordPollCycleAsync`, always stamps `PolledUtc` as "now," so backdating can only happen by seeding rows directly in a test).

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Ingestion/PollCycleRollupTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Ingestion;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Ingestion;

[Collection("Postgres")]
public sealed class PollCycleRollupTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public PollCycleRollupTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    [Fact]
    public async Task RollUpStalePollCyclesAsync_LeavesRecentRowsAlone()
    {
        using var context = CreateContext();
        context.PollCycles.Add(new PollCycle
        {
            PolledUtc = DateTimeOffset.UtcNow.AddDays(-1),
            MessagesChecked = 3,
            ReportsParsed = 3,
            ParseFailures = 0,
            Succeeded = true
        });
        await context.SaveChangesAsync();

        await PollingService.RollUpStalePollCyclesAsync(context, CancellationToken.None);

        using var verify = CreateContext();
        Assert.Single(verify.PollCycles);
        Assert.Empty(verify.PollCycleDailySummaries);
    }

    [Fact]
    public async Task RollUpStalePollCyclesAsync_RollsUpAndDeletesRowsOlderThanSevenDays()
    {
        using var context = CreateContext();
        var staleDay = DateTimeOffset.UtcNow.Date.AddDays(-10);
        context.PollCycles.Add(new PollCycle { PolledUtc = staleDay.AddHours(1), MessagesChecked = 5, ReportsParsed = 4, ParseFailures = 1, Succeeded = true });
        context.PollCycles.Add(new PollCycle { PolledUtc = staleDay.AddHours(2), MessagesChecked = 2, ReportsParsed = 0, ParseFailures = 0, Succeeded = false, ErrorMessage = "boom" });
        await context.SaveChangesAsync();

        await PollingService.RollUpStalePollCyclesAsync(context, CancellationToken.None);

        using var verify = CreateContext();
        Assert.Empty(verify.PollCycles);
        var summary = verify.PollCycleDailySummaries.Single();
        Assert.Equal(DateOnly.FromDateTime(staleDay.UtcDateTime), summary.Date);
        Assert.Equal(2, summary.TotalCycles);
        Assert.Equal(1, summary.SuccessfulCycles);
        Assert.Equal(1, summary.FailedCycles);
        Assert.Equal(7, summary.TotalMessagesChecked);
        Assert.Equal(4, summary.TotalReportsParsed);
        Assert.Equal(1, summary.TotalParseFailures);
    }

    [Fact]
    public async Task RollUpStalePollCyclesAsync_AddsToAnExistingSummaryRow_InsteadOfDuplicatingIt()
    {
        using var context = CreateContext();
        var staleDay = DateTimeOffset.UtcNow.Date.AddDays(-10);
        var dateOnly = DateOnly.FromDateTime(staleDay.UtcDateTime);
        context.PollCycleDailySummaries.Add(new PollCycleDailySummary
        {
            Date = dateOnly,
            TotalCycles = 5,
            SuccessfulCycles = 5,
            FailedCycles = 0,
            TotalMessagesChecked = 10,
            TotalReportsParsed = 10,
            TotalParseFailures = 0
        });
        context.PollCycles.Add(new PollCycle { PolledUtc = staleDay.AddHours(1), MessagesChecked = 1, ReportsParsed = 1, ParseFailures = 0, Succeeded = true });
        await context.SaveChangesAsync();

        await PollingService.RollUpStalePollCyclesAsync(context, CancellationToken.None);

        using var verify = CreateContext();
        var summary = verify.PollCycleDailySummaries.Single();
        Assert.Equal(6, summary.TotalCycles);
        Assert.Equal(11, summary.TotalMessagesChecked);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter PollCycleRollupTests`
Expected: FAIL to build — `PollingService.RollUpStalePollCyclesAsync` doesn't exist yet.

- [ ] **Step 3: Write the implementation**

In `src/DotMarc/Ingestion/PollingService.cs`, add this method after `RecordPollCycleAsync` (added in Task 2):

```csharp
    /// <summary>Folds any PollCycle row belonging to a UTC calendar day more than 7 days in the
    /// past into that day's PollCycleDailySummary, then deletes the raw rows. internal (not
    /// private) so tests can call it directly against hand-seeded, backdated rows — the only
    /// production caller, RecordPollCycleAsync, always writes PolledUtc as "now," so there's no
    /// other way to exercise the &gt;7-day-old path deterministically. Anchored to a calendar-day
    /// boundary rather than a rolling timestamp: a day is only eligible once every one of its rows
    /// is already more than 7 days old, so it's only ever rolled up once, with nothing to merge
    /// across passes.</summary>
    internal static async Task RollUpStalePollCyclesAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var cutoffUtc = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(-7);

        var staleRows = await context.PollCycles
            .Where(p => p.PolledUtc < cutoffUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (staleRows.Count == 0)
        {
            return;
        }

        foreach (var group in staleRows.GroupBy(p => DateOnly.FromDateTime(p.PolledUtc.UtcDateTime)))
        {
            var summary = await context.PollCycleDailySummaries
                .SingleOrDefaultAsync(s => s.Date == group.Key, cancellationToken)
                .ConfigureAwait(false);

            if (summary is null)
            {
                summary = new PollCycleDailySummary { Date = group.Key };
                context.PollCycleDailySummaries.Add(summary);
            }

            summary.TotalCycles += group.Count();
            summary.SuccessfulCycles += group.Count(p => p.Succeeded);
            summary.FailedCycles += group.Count(p => !p.Succeeded);
            summary.TotalMessagesChecked += group.Sum(p => p.MessagesChecked);
            summary.TotalReportsParsed += group.Sum(p => p.ReportsParsed);
            summary.TotalParseFailures += group.Sum(p => p.ParseFailures);
        }

        context.PollCycles.RemoveRange(staleRows);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
```

Then wire it into `RecordPollCycleAsync` (from Task 2) — add one line at the end of the method, after the existing `await context.SaveChangesAsync(...)`:

```csharp
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await RollUpStalePollCyclesAsync(context, cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter PollCycleRollupTests`
Expected: PASS.

- [ ] **Step 5: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS. (This also confirms Task 2's tests still pass now that every recorded cycle also triggers a rollup check — they should, since none of Task 2's test data is more than 7 days old.)

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Ingestion/PollingService.cs test/DotMarc.Tests/Ingestion/PollCycleRollupTests.cs
git commit -m "Roll up PollCycle rows older than 7 days into PollCycleDailySummary"
```

---

### Task 4: Dashboard poll-status panel

**Files:**
- Modify: `src/DotMarc/Components/Pages/Dashboard.razor`

**Interfaces:**
- Consumes: `DotMarcDbContext.PollCycles` (Task 1); `IDbContextFactory<DotMarcDbContext>` (already injected in this file).

This task has no automated tests: consistent with `ManageDomains.razor`/`ConfirmDeleteDomainDialog.razor` (this project has no Blazor component-rendering test framework — no bUnit anywhere in the suite). Verification is a build check plus a manual check when the environment allows one.

- [ ] **Step 1: Add the panel markup**

In `src/DotMarc/Components/Pages/Dashboard.razor`, insert this new `MudPaper` block immediately after the closing `</MudTable>` tag and before the closing `}` of the `else` block:

```razor
    <MudPaper Class="pa-4 mt-4" Elevation="1">
        <MudText Typo="Typo.subtitle1" Class="mb-2">Polling status</MudText>
        @if (_lastPoll is null)
        {
            <MudText Typo="Typo.body2">Not polled yet.</MudText>
        }
        else if (_lastPoll.Succeeded)
        {
            <MudGrid>
                <MudItem xs="12" sm="3">
                    <MudText Typo="Typo.caption">Last polled</MudText>
                    <MudText>@_lastPoll.PolledUtc.ToString("O")</MudText>
                </MudItem>
                <MudItem xs="12" sm="3">
                    <MudText Typo="Typo.caption">Messages checked</MudText>
                    <MudText>@_lastPoll.MessagesChecked</MudText>
                </MudItem>
                <MudItem xs="12" sm="3">
                    <MudText Typo="Typo.caption">Reports parsed</MudText>
                    <MudText>@_lastPoll.ReportsParsed</MudText>
                </MudItem>
                <MudItem xs="12" sm="3">
                    <MudText Typo="Typo.caption">Parse failures</MudText>
                    <MudText>@_lastPoll.ParseFailures</MudText>
                </MudItem>
            </MudGrid>
        }
        else
        {
            <MudText Color="Color.Error">Last poll (@_lastPoll.PolledUtc.ToString("O")) failed: @_lastPoll.ErrorMessage</MudText>
        }
    </MudPaper>
```

- [ ] **Step 2: Load the latest poll cycle**

In the `@code` block, add a field alongside the existing `_summary`/`_rows` fields:

```csharp
    private PollStatusRow? _lastPoll;
```

In `LoadAsync`, add this query using the same `db` context the method already creates, anywhere after `await using var db = await DbFactory.CreateDbContextAsync();` and before the method returns:

```csharp
        _lastPoll = await db.PollCycles
            .AsNoTracking()
            .OrderByDescending(p => p.PolledUtc)
            .Select(p => new PollStatusRow(p.PolledUtc, p.MessagesChecked, p.ReportsParsed, p.ParseFailures, p.Succeeded, p.ErrorMessage))
            .FirstOrDefaultAsync();
```

Add the new record alongside the existing `DashboardSummary`/`DomainRow` records at the bottom of the `@code` block:

```csharp
    private sealed record PollStatusRow(DateTimeOffset PolledUtc, int MessagesChecked, int ReportsParsed, int ParseFailures, bool Succeeded, string? ErrorMessage);
```

- [ ] **Step 3: Build to confirm it compiles**

Run: `dotnet build dotMARC.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 4: Manual verification**

If the environment allows running the app (`docker compose up postgres` plus `dotnet run --project src/DotMarc/DotMarc.csproj` with the Graph/EntraId env vars set, per the README's Development section) and signing in: confirm the new "Polling status" panel appears below the domain table, shows "Not polled yet." before the background service's first cycle completes, then updates to show a real ISO 8601 timestamp and counts once it has. If the environment doesn't allow this (no local Postgres port available, no interactive Entra sign-in — a known, previously-hit limitation in this project's sandboxed environments), report clearly in your report which steps you could and couldn't perform, and why — this is an acceptable, expected limitation, not a blocker.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Components/Pages/Dashboard.razor
git commit -m "Show last poll status on the Dashboard"
```
