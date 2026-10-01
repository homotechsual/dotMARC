using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Audit;

[Collection("Postgres")]
public sealed class AuditLogTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AuditLogTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task Record_IsSavedByTheCallersOwnSave()
    {
        await using (var context = CreateContext())
        {
            AuditLog.Record(context, TestActors.Admin, AuditActions.DomainsReordered, null, "Reordered domains");
            await context.SaveChangesAsync();
        }

        await using var verify = CreateContext();
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal((AuditEntryKind.Change, "Test Admin", "admin@example.com"), (entry.Kind, entry.ActorName, entry.ActorEmail));
    }

    [Fact]
    public async Task SaveAndRecordAsync_GivesTheEntryTheNewRowsId()
    {
        await using (var context = CreateContext())
        {
            var group = new Group { Name = "Client A" };
            context.Groups.Add(group);
            await AuditLog.SaveAndRecordAsync(context, () => AuditLog.Record(context, TestActors.Admin, AuditActions.GroupAdded, AuditTarget.For(group), "Added group Client A"), CancellationToken.None);
        }

        await using var verify = CreateContext();
        var savedGroup = await verify.Groups.SingleAsync();
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(savedGroup.Id.ToString(), entry.TargetId);
    }

    [Fact]
    public async Task SaveAndRecordAsync_JoinsTheCallersTransaction()
    {
        await using (var context = CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            var group = new Group { Name = "Client A" };
            context.Groups.Add(group);
            await AuditLog.SaveAndRecordAsync(context, () => AuditLog.Record(context, TestActors.Admin, AuditActions.GroupAdded, AuditTarget.For(group), "Added group Client A"), CancellationToken.None);
            await transaction.RollbackAsync();
        }

        await using var verify = CreateContext();
        Assert.Empty(verify.Groups);
        Assert.Empty(verify.AuditEntries);
    }

    [Fact]
    public async Task SaveAndRecordAsync_KeepsNeither_WhenTheEntryCannotBeSaved()
    {
        await using (var context = CreateContext())
        {
            context.Groups.Add(new Group { Name = "Client A" });
            // Action is limited to 64 characters, so this entry fails to save after the group already has.
            var tooLongAction = new string('x', 65);
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                AuditLog.SaveAndRecordAsync(context, () => AuditLog.Record(context, TestActors.Admin, tooLongAction, null, "Broken"), CancellationToken.None));
        }

        await using var verify = CreateContext();
        Assert.Empty(verify.Groups);
        Assert.Empty(verify.AuditEntries);
    }
}
