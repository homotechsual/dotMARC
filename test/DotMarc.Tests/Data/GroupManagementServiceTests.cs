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

        var result = await GroupManagementService.AddGroupAsync(context, "  Client A  ", CancellationToken.None);

        Assert.Equal(GroupManagementService.AddGroupResult.Added, result);
        using var verify = CreateContext();
        Assert.Equal("Client A", verify.Groups.Single().Name);
    }

    [Fact]
    public async Task AddGroupAsync_RejectsEmptyName()
    {
        using var context = CreateContext();

        var result = await GroupManagementService.AddGroupAsync(context, "   ", CancellationToken.None);

        Assert.Equal(GroupManagementService.AddGroupResult.InvalidName, result);
        Assert.Empty(context.Groups);
    }

    [Fact]
    public async Task AddGroupAsync_RejectsCaseInsensitiveDuplicate()
    {
        using var context = CreateContext();
        await GroupManagementService.AddGroupAsync(context, "Client A", CancellationToken.None);

        var result = await GroupManagementService.AddGroupAsync(context, "client a", CancellationToken.None);

        Assert.Equal(GroupManagementService.AddGroupResult.AlreadyExists, result);
    }

    [Fact]
    public async Task RenameGroupAsync_RenamesTheGroup()
    {
        using var context = CreateContext();
        await GroupManagementService.AddGroupAsync(context, "Client A", CancellationToken.None);
        var groupId = context.Groups.Single().Id;

        var result = await GroupManagementService.RenameGroupAsync(context, groupId, "Client A Renamed", CancellationToken.None);

        Assert.Equal(GroupManagementService.AddGroupResult.Added, result);
        using var verify = CreateContext();
        Assert.Equal("Client A Renamed", verify.Groups.Single().Name);
    }

    [Fact]
    public async Task RenameGroupAsync_RejectsRenamingToAnExistingName()
    {
        using var context = CreateContext();
        await GroupManagementService.AddGroupAsync(context, "Client A", CancellationToken.None);
        await GroupManagementService.AddGroupAsync(context, "Client B", CancellationToken.None);
        var clientBId = context.Groups.Single(g => g.Name == "Client B").Id;

        var result = await GroupManagementService.RenameGroupAsync(context, clientBId, "Client A", CancellationToken.None);

        Assert.Equal(GroupManagementService.AddGroupResult.AlreadyExists, result);
    }

    [Fact]
    public async Task RemoveGroupAsync_RemovesTheGroup_ButNotItsMemberDomain()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, "contoso.io", CancellationToken.None);
        await GroupManagementService.AddGroupAsync(context, "Client A", CancellationToken.None);
        var domainId = context.Domains.Single().Id;
        var groupId = context.Groups.Single().Id;
        await GroupManagementService.SetDomainGroupsAsync(context, domainId, [groupId], CancellationToken.None);

        await GroupManagementService.RemoveGroupAsync(context, groupId, CancellationToken.None);

        using var verify = CreateContext();
        Assert.Empty(verify.Groups);
        Assert.NotNull(verify.Domains.SingleOrDefault(d => d.Id == domainId));
    }

    [Fact]
    public async Task SetDomainGroupsAsync_ReplacesTheFullMembershipSet()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, "contoso.io", CancellationToken.None);
        await GroupManagementService.AddGroupAsync(context, "Client A", CancellationToken.None);
        await GroupManagementService.AddGroupAsync(context, "Client B", CancellationToken.None);
        var domainId = context.Domains.Single().Id;
        var groupAId = context.Groups.Single(g => g.Name == "Client A").Id;
        var groupBId = context.Groups.Single(g => g.Name == "Client B").Id;

        await GroupManagementService.SetDomainGroupsAsync(context, domainId, [groupAId, groupBId], CancellationToken.None);
        using (var verify1 = CreateContext())
        {
            var domain = verify1.Domains.Include(d => d.Groups).Single();
            Assert.Equal(2, domain.Groups.Count);
        }

        await GroupManagementService.SetDomainGroupsAsync(context, domainId, [groupAId], CancellationToken.None);

        using var verify2 = CreateContext();
        var updated = verify2.Domains.Include(d => d.Groups).Single();
        Assert.Single(updated.Groups);
        Assert.Equal("Client A", updated.Groups[0].Name);
    }
}
