using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Audit;

[Collection("Postgres")]
public sealed class AuditRetentionTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AuditRetentionTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static AuditEntry EntryAged(AuditEntryKind kind, int daysOld) => new()
    {
        Kind = kind, ActorName = "Someone", Action = "test.action", Summary = $"{kind} {daysOld} days old", OccurredUtc = Now.AddDays(-daysOld)
    };

    [Fact]
    public async Task PurgeAsync_RemovesEachKindAfterItsOwnPeriod()
    {
        await using (var context = CreateContext())
        {
            context.AuditEntries.AddRange(
                EntryAged(AuditEntryKind.PageView, 89), EntryAged(AuditEntryKind.PageView, 91),
                EntryAged(AuditEntryKind.SignIn, 364), EntryAged(AuditEntryKind.SignIn, 366),
                EntryAged(AuditEntryKind.Change, 366));
            await context.SaveChangesAsync();

            var deleted = await AuditRetention.PurgeAsync(context, Now);

            Assert.Equal(3, deleted);
        }

        await using var verify = CreateContext();
        Assert.Equal(["PageView 89 days old", "SignIn 364 days old"], await verify.AuditEntries.OrderBy(entry => entry.Summary).Select(entry => entry.Summary).ToListAsync());
    }

    [Fact]
    public async Task PurgeAsync_KeepsEverything_ForAKindKeptForever()
    {
        await using (var context = CreateContext())
        {
            var settings = await context.AuditSettings.SingleAsync();
            settings.ChangeRetentionDays = null;
            context.AuditEntries.Add(EntryAged(AuditEntryKind.Change, 5000));
            await context.SaveChangesAsync();

            await AuditRetention.PurgeAsync(context, Now);
        }

        await using var verify = CreateContext();
        Assert.Single(verify.AuditEntries);
    }
}