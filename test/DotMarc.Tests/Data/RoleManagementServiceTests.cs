using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class RoleManagementServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public RoleManagementServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task AddRoleAsync_AddsARoleWithTheGivenPermissions()
    {
        using var context = CreateContext();

        var result = await RoleManagementService.AddRoleAsync(context, "Domain Manager", [Permission.DomainsView, Permission.DomainsAdd], CancellationToken.None);

        Assert.Equal(RoleManagementService.AddRoleResult.Added, result);
        using var verify = CreateContext();
        var role = verify.Roles.Single();
        Assert.Equal("Domain Manager", role.Name);
        Assert.False(role.IsLocked);
        Assert.False(role.IsScopable);
        Assert.Equal(2, role.Permissions.Count);
    }

    [Fact]
    public async Task AddRoleAsync_RejectsEmptyName()
    {
        using var context = CreateContext();

        var result = await RoleManagementService.AddRoleAsync(context, "   ", [Permission.DomainsView], CancellationToken.None);

        Assert.Equal(RoleManagementService.AddRoleResult.InvalidName, result);
        Assert.Empty(context.Roles);
    }

    [Fact]
    public async Task AddRoleAsync_RejectsCaseInsensitiveDuplicate()
    {
        using var context = CreateContext();
        await RoleManagementService.AddRoleAsync(context, "Domain Manager", [Permission.DomainsView], CancellationToken.None);

        var result = await RoleManagementService.AddRoleAsync(context, "domain manager", [Permission.DomainsView], CancellationToken.None);

        Assert.Equal(RoleManagementService.AddRoleResult.AlreadyExists, result);
    }

    [Fact]
    public async Task UpdateRoleAsync_UpdatesNameAndPermissions()
    {
        using var context = CreateContext();
        await RoleManagementService.AddRoleAsync(context, "Domain Manager", [Permission.DomainsView], CancellationToken.None);
        var roleId = context.Roles.Single().Id;

        var result = await RoleManagementService.UpdateRoleAsync(context, roleId, "Renamed", [Permission.DomainsView, Permission.DomainsDelete], CancellationToken.None);

        Assert.Equal(RoleManagementService.UpdateRoleResult.Updated, result);
        using var verify = CreateContext();
        var role = verify.Roles.Single();
        Assert.Equal("Renamed", role.Name);
        Assert.Equal(2, role.Permissions.Count);
    }

    [Fact]
    public async Task UpdateRoleAsync_RejectsChangesToALockedRole()
    {
        using var context = CreateContext();
        context.Roles.Add(new Role { Name = "Admin", IsLocked = true, IsScopable = false, Permissions = [Permission.AccessManage] });
        context.SaveChanges();
        var adminId = context.Roles.Single().Id;

        var result = await RoleManagementService.UpdateRoleAsync(context, adminId, "Not Admin", [Permission.DomainsView], CancellationToken.None);

        Assert.Equal(RoleManagementService.UpdateRoleResult.Locked, result);
        using var verify = CreateContext();
        Assert.Equal("Admin", verify.Roles.Single().Name);
    }

    [Fact]
    public async Task RemoveRoleAsync_RejectsRemovingALockedRole()
    {
        using var context = CreateContext();
        context.Roles.Add(new Role { Name = "Admin", IsLocked = true, IsScopable = false, Permissions = [Permission.AccessManage] });
        context.SaveChanges();
        var adminId = context.Roles.Single().Id;

        var result = await RoleManagementService.RemoveRoleAsync(context, adminId, CancellationToken.None);

        Assert.Equal(RoleManagementService.RemoveRoleResult.Locked, result);
    }

    [Fact]
    public async Task RemoveRoleAsync_RejectsRemovingARoleStillGrantedToSomeone()
    {
        using var context = CreateContext();
        await RoleManagementService.AddRoleAsync(context, "Domain Manager", [Permission.DomainsView], CancellationToken.None);
        var roleId = context.Roles.Single().Id;
        await UserAccessManagementService.GrantAccessAsync(context, "person@example.com", roleId, [], CancellationToken.None);

        var result = await RoleManagementService.RemoveRoleAsync(context, roleId, CancellationToken.None);

        Assert.Equal(RoleManagementService.RemoveRoleResult.InUse, result);
        using var verify = CreateContext();
        Assert.NotEmpty(verify.Roles);
    }

    [Fact]
    public async Task RemoveRoleAsync_RemovesAnUnusedUnlockedRole()
    {
        using var context = CreateContext();
        await RoleManagementService.AddRoleAsync(context, "Domain Manager", [Permission.DomainsView], CancellationToken.None);
        var roleId = context.Roles.Single().Id;

        var result = await RoleManagementService.RemoveRoleAsync(context, roleId, CancellationToken.None);

        Assert.Equal(RoleManagementService.RemoveRoleResult.Removed, result);
        using var verify = CreateContext();
        Assert.Empty(verify.Roles);
    }
}
