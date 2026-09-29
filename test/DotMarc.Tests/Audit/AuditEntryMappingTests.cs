using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Audit;

[Collection("Postgres")]
public sealed class AuditEntryMappingTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AuditEntryMappingTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task AnEntry_RoundTripsItsKindsAndFieldChanges()
    {
        await using (var context = CreateContext())
        {
            context.AuditEntries.Add(new AuditEntry
            {
                OccurredUtc = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero),
                Kind = AuditEntryKind.Change,
                ActorKind = AuditActorKind.User,
                ActorEmail = "admin@example.com",
                ActorName = "Test Admin",
                Action = "group.renamed",
                TargetType = "Group",
                TargetId = "7",
                TargetName = "Client B",
                Summary = "Renamed group Client A to Client B",
                Changes = [new AuditFieldChange("Name", "Client A", "Client B"), new AuditFieldChange("Client secret", null, null, Secret: true)]
            });
            await context.SaveChangesAsync();
        }

        await using var verify = CreateContext();
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditEntryKind.Change, entry.Kind);
        Assert.Equal(AuditActorKind.User, entry.ActorKind);
        Assert.Equal(
            [new AuditFieldChange("Name", "Client A", "Client B"), new AuditFieldChange("Client secret", null, null, true)],
            entry.Changes);
    }

    [Fact]
    public async Task KindsAreStoredAsText()
    {
        await using (var context = CreateContext())
        {
            context.AuditEntries.Add(new AuditEntry { OccurredUtc = DateTimeOffset.UtcNow, Kind = AuditEntryKind.SignIn, ActorName = "Someone", Action = "signin.succeeded", Summary = "Someone signed in" });
            await context.SaveChangesAsync();
        }

        await using var verify = CreateContext();
        var storedKind = await verify.Database.SqlQueryRaw<string>("SELECT \"Kind\" AS \"Value\" FROM \"AuditEntries\"").SingleAsync();
        Assert.Equal("SignIn", storedKind);
    }

    [Fact]
    public async Task TheRetentionSettingsRowIsSeededWithTheDefaults()
    {
        await using var context = CreateContext();

        var settings = await context.AuditSettings.SingleAsync();

        Assert.Equal((365, 365, 90), (settings.ChangeRetentionDays, settings.SignInRetentionDays, settings.PageViewRetentionDays));
    }
}
