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
        var staleDay = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero).AddDays(-10);
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
        var staleDay = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero).AddDays(-10);
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
