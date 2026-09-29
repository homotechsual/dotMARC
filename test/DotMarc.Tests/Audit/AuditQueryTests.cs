using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Audit;

[Collection("Postgres")]
public sealed class AuditQueryTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AuditQueryTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        context.AuditEntries.AddRange(
            Entry(1, AuditEntryKind.Change, "Sam Jones", AuditActions.GroupRenamed, "Client B", "Renamed group Client A to Client B", new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero)),
            Entry(2, AuditEntryKind.Change, "Alex Kim", AuditActions.NotificationSettingsSaved, "Notifications", "Set the non-benign threshold to 100%", new DateTimeOffset(2026, 9, 29, 23, 59, 0, TimeSpan.Zero)),
            Entry(3, AuditEntryKind.SignIn, "Sam Jones", AuditActions.SignInSucceeded, null, "Sam Jones signed in", new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero)),
            Entry(4, AuditEntryKind.PageView, "Sam Jones", AuditActions.PageViewed, "contoso.com", "Opened /domains/contoso.com", new DateTimeOffset(2026, 9, 15, 8, 1, 0, TimeSpan.Zero)),
            Entry(5, AuditEntryKind.Change, "Alex Kim", AuditActions.NotificationSettingsSaved, "Notifications", "Set the non-benign threshold to 1000", new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero)));
        await context.SaveChangesAsync();
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

    private static AuditEntry Entry(long id, AuditEntryKind kind, string actorName, string action, string? targetName, string summary, DateTimeOffset occurredUtc) => new()
    {
        Id = id, Kind = kind, ActorName = actorName, ActorEmail = $"{actorName.Split(' ')[0].ToLowerInvariant()}@contoso.com",
        Action = action, TargetName = targetName, Summary = summary, OccurredUtc = occurredUtc
    };

    private async Task<long[]> MatchingIdsAsync(AuditFilter filter)
    {
        await using var context = CreateContext();
        return await AuditQuery.Apply(context.AuditEntries, filter).OrderBy(entry => entry.Id).Select(entry => entry.Id).ToArrayAsync();
    }

    [Fact]
    public async Task PageViewsAreHidden_UnlessAskedFor()
    {
        Assert.Equal(new long[] { 1, 2, 3, 5 }, await MatchingIdsAsync(new AuditFilter()));
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, await MatchingIdsAsync(new AuditFilter { IncludePageViews = true }));
        Assert.Equal(new long[] { 4 }, await MatchingIdsAsync(new AuditFilter { Kind = AuditEntryKind.PageView }));
    }

    [Fact]
    public async Task TheToDate_IncludesTheWholeDay()
    {
        Assert.Equal(new long[] { 1, 2, 3 }, await MatchingIdsAsync(new AuditFilter { From = new DateOnly(2026, 9, 10), To = new DateOnly(2026, 9, 29) }));
    }

    [Fact]
    public async Task Who_MatchesTheNameOrEmail_IgnoringCase()
    {
        Assert.Equal(new long[] { 1, 3 }, await MatchingIdsAsync(new AuditFilter { Who = "SAM" }));
        Assert.Equal(new long[] { 2, 5 }, await MatchingIdsAsync(new AuditFilter { Who = "alex@contoso" }));
    }

    [Fact]
    public async Task SearchText_TreatsPercentAndUnderscoreLiterally()
    {
        Assert.Equal(new long[] { 2 }, await MatchingIdsAsync(new AuditFilter { Summary = "100%" }));
        Assert.Empty(await MatchingIdsAsync(new AuditFilter { Summary = "threshold_to" }));
    }

    [Fact]
    public async Task ActionAndTarget_Narrow()
    {
        Assert.Equal(new long[] { 1 }, await MatchingIdsAsync(new AuditFilter { Action = AuditActions.GroupRenamed }));
        Assert.Equal(new long[] { 2, 5 }, await MatchingIdsAsync(new AuditFilter { Target = "notif" }));
    }
}
