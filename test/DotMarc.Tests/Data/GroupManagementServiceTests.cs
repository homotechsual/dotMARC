using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class GroupManagementServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public GroupManagementServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task AddGroupAsync_AddsATrimmedGroup()
    {
        using var context = CreateContext();

        var result = await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "  Client A  ", CancellationToken.None);

        Assert.Equal(GroupManagementService.AddGroupResult.Added, result);
        using var verify = CreateContext();
        Assert.Equal("Client A", verify.Groups.Single().Name);
    }

    [Fact]
    public async Task AddGroupAsync_LinksTheNewGroupToTheGivenHaloClient()
    {
        using var context = CreateContext();

        var result = await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Compute (Bridgend) Limited", CancellationToken.None, haloClientId: 37);

        Assert.Equal(GroupManagementService.AddGroupResult.Added, result);
        using var verify = CreateContext();
        Assert.Equal(37, verify.Groups.Single().HaloClientId);
    }

    [Fact]
    public async Task AddGroupAsync_LeavesTheHaloClientUnsetByDefault()
    {
        using var context = CreateContext();

        await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client A", CancellationToken.None);

        using var verify = CreateContext();
        Assert.Null(verify.Groups.Single().HaloClientId);
    }

    [Fact]
    public async Task AddGroupAsync_RejectsEmptyName()
    {
        using var context = CreateContext();

        var result = await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "   ", CancellationToken.None);

        Assert.Equal(GroupManagementService.AddGroupResult.InvalidName, result);
        Assert.Empty(context.Groups);
    }

    [Fact]
    public async Task AddGroupAsync_RejectsCaseInsensitiveDuplicate()
    {
        using var context = CreateContext();
        await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client A", CancellationToken.None);

        var result = await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "client a", CancellationToken.None);

        Assert.Equal(GroupManagementService.AddGroupResult.AlreadyExists, result);
    }

    [Fact]
    public async Task RenameGroupAsync_RenamesTheGroup()
    {
        using var context = CreateContext();
        await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client A", CancellationToken.None);
        var groupId = context.Groups.Single().Id;

        var result = await GroupManagementService.RenameGroupAsync(context, TestActors.Admin, groupId, "Client A Renamed", CancellationToken.None);

        Assert.Equal(GroupManagementService.AddGroupResult.Added, result);
        using var verify = CreateContext();
        Assert.Equal("Client A Renamed", verify.Groups.Single().Name);
    }

    [Fact]
    public async Task RenameGroupAsync_RejectsRenamingToAnExistingName()
    {
        using var context = CreateContext();
        await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client A", CancellationToken.None);
        await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client B", CancellationToken.None);
        var clientBId = context.Groups.Single(g => g.Name == "Client B").Id;

        var result = await GroupManagementService.RenameGroupAsync(context, TestActors.Admin, clientBId, "Client A", CancellationToken.None);

        Assert.Equal(GroupManagementService.AddGroupResult.AlreadyExists, result);
    }

    [Fact]
    public async Task RemoveGroupAsync_RemovesTheGroup_ButNotItsMemberDomain()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.io", CancellationToken.None);
        await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client A", CancellationToken.None);
        var domainId = context.Domains.Single().Id;
        var groupId = context.Groups.Single().Id;
        await GroupManagementService.SetDomainGroupsAsync(context, TestActors.Admin, domainId, [groupId], CancellationToken.None);

        await GroupManagementService.RemoveGroupAsync(context, TestActors.Admin, groupId, CancellationToken.None);

        using var verify = CreateContext();
        Assert.Empty(verify.Groups);
        Assert.NotNull(verify.Domains.SingleOrDefault(d => d.Id == domainId));
    }

    [Fact]
    public async Task SetDomainGroupsAsync_ReplacesTheFullMembershipSet()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.io", CancellationToken.None);
        await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client A", CancellationToken.None);
        await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client B", CancellationToken.None);
        var domainId = context.Domains.Single().Id;
        var groupAId = context.Groups.Single(g => g.Name == "Client A").Id;
        var groupBId = context.Groups.Single(g => g.Name == "Client B").Id;

        await GroupManagementService.SetDomainGroupsAsync(context, TestActors.Admin, domainId, [groupAId, groupBId], CancellationToken.None);
        using (var verify1 = CreateContext())
        {
            var domain = verify1.Domains.Include(d => d.Groups).Single();
            Assert.Equal(2, domain.Groups.Count);
        }

        await GroupManagementService.SetDomainGroupsAsync(context, TestActors.Admin, domainId, [groupAId], CancellationToken.None);

        using var verify2 = CreateContext();
        var updated = verify2.Domains.Include(d => d.Groups).Single();
        Assert.Single(updated.Groups);
        Assert.Equal("Client A", updated.Groups[0].Name);
    }

    [Fact]
    public async Task SetHaloClientIdAsync_UpdatesTheGroupsMapping()
    {
        await using var context = CreateContext();
        var group = new Group { Name = "Client A" };
        context.Groups.Add(group);
        await context.SaveChangesAsync();

        await GroupManagementService.SetHaloClientIdAsync(context, TestActors.Admin, group.Id, 42);

        await using var verify = CreateContext();
        Assert.Equal(42, (await verify.Groups.SingleAsync(g => g.Id == group.Id)).HaloClientId);
    }

    [Fact]
    public async Task SetHaloClientIdAsync_ClearsTheMapping_WhenPassedNull()
    {
        await using var context = CreateContext();
        var group = new Group { Name = "Client A", HaloClientId = 42 };
        context.Groups.Add(group);
        await context.SaveChangesAsync();

        await GroupManagementService.SetHaloClientIdAsync(context, TestActors.Admin, group.Id, null);

        await using var verify = CreateContext();
        Assert.Null((await verify.Groups.SingleAsync(g => g.Id == group.Id)).HaloClientId);
    }

    [Fact]
    public async Task AddGroupAsync_RecordsTheNewGroupAndItsHaloClient()
    {
        await using (var context = CreateContext())
        {
            await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client A", CancellationToken.None, haloClientId: 37);
        }

        await using var verify = CreateContext();
        var group = await verify.Groups.SingleAsync();
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal((AuditActions.GroupAdded, group.Id.ToString(), "Added group Client A"), (entry.Action, entry.TargetId, entry.Summary));
        Assert.Equal([new AuditFieldChange("Halo client", null, "37")], entry.Changes);
    }

    [Fact]
    public async Task RenameGroupAsync_RecordsTheOldAndNewName()
    {
        await using (var context = CreateContext())
        {
            await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client A", CancellationToken.None);
            var groupId = (await context.Groups.SingleAsync()).Id;
            await GroupManagementService.RenameGroupAsync(context, TestActors.Admin, groupId, "Client B");
        }

        await using var verify = CreateContext();
        var entry = await verify.AuditEntries.SingleAsync(auditEntry => auditEntry.Action == AuditActions.GroupRenamed);
        Assert.Equal("Renamed group Client A to Client B", entry.Summary);
        Assert.Equal([new AuditFieldChange("Name", "Client A", "Client B")], entry.Changes);
    }

    [Fact]
    public async Task RenameGroupAsync_RecordsNothing_WhenTheNameIsTaken()
    {
        await using (var context = CreateContext())
        {
            await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client A", CancellationToken.None);
            await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client B", CancellationToken.None);
            var clientBId = (await context.Groups.SingleAsync(group => group.Name == "Client B")).Id;
            await GroupManagementService.RenameGroupAsync(context, TestActors.Admin, clientBId, "client a");
        }

        await using var verify = CreateContext();
        Assert.DoesNotContain(verify.AuditEntries, entry => entry.Action == AuditActions.GroupRenamed);
    }

    [Fact]
    public async Task SetDomainGroupsAsync_RecordsTheGroupsBeforeAndAfter()
    {
        await using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com");
            await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client A", CancellationToken.None);
            await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client B", CancellationToken.None);
            var domainId = (await context.Domains.SingleAsync()).Id;
            var groupIdsByName = await context.Groups.ToDictionaryAsync(group => group.Name, group => group.Id);
            await GroupManagementService.SetDomainGroupsAsync(context, TestActors.Admin, domainId, [groupIdsByName["Client A"]]);
            await GroupManagementService.SetDomainGroupsAsync(context, TestActors.Admin, domainId, [groupIdsByName["Client A"], groupIdsByName["Client B"]]);
        }

        await using var verify = CreateContext();
        var entry = await verify.AuditEntries.Where(auditEntry => auditEntry.Action == AuditActions.DomainGroupsChanged).OrderByDescending(auditEntry => auditEntry.Id).FirstAsync();
        Assert.Equal("Changed the groups for contoso.com", entry.Summary);
        Assert.Equal([new AuditFieldChange("Groups", "Client A", "Client A, Client B")], entry.Changes);
    }

    [Fact]
    public async Task RemoveGroup_IsRefused_WhileAnUnrevokedApiKeyIsLimitedToIt_AndAllowedOnceRevoked()
    {
        int groupId;
        int keyId;
        await using (var context = CreateContext())
        {
            var group = new Group { Name = "Offboarded client" };
            var role = new Role { Name = "Client viewer", IsScopable = true, Permissions = [Permission.DomainsView] };
            context.Groups.Add(group);
            context.Roles.Add(role);
            await context.SaveChangesAsync();
            groupId = group.Id;
            keyId = (await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, "Client portal", role.Id, [groupId], 90)).Key!.Id;
        }

        await using (var context = CreateContext())
        {
            // Deleting the group would leave the key with no groups, which means every group.
            Assert.Equal(GroupManagementService.RemoveGroupResult.InUseByApiKey, await GroupManagementService.RemoveGroupAsync(context, TestActors.Admin, groupId));
            await ApiKeyManagementService.RevokeAsync(context, TestActors.Admin, keyId);
        }

        await using (var context = CreateContext())
        {
            Assert.Equal(GroupManagementService.RemoveGroupResult.Removed, await GroupManagementService.RemoveGroupAsync(context, TestActors.Admin, groupId));
        }

        await using var verify = CreateContext();
        Assert.False(await verify.Groups.AnyAsync(group => group.Id == groupId));
    }
}
