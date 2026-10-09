using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class UserAccessManagementServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public UserAccessManagementServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private int SeedRole(DotMarcDbContext context, string name, bool isScopable, params Permission[] permissions)
    {
        var role = new Role { Name = name, IsLocked = false, IsScopable = isScopable, Permissions = [.. permissions] };
        context.Roles.Add(role);
        context.SaveChanges();
        return role.Id;
    }

    [Fact]
    public async Task GrantAccessAsync_GrantsAnUnscopedRoleWithNoGroups()
    {
        using var context = CreateContext();
        var roleId = SeedRole(context, "Domain Manager", isScopable: false, Permission.DomainsView);

        var result = await UserAccessManagementService.GrantAccessAsync(context, TestActors.Admin, "  person@example.com  ", roleId, [999], CancellationToken.None);

        Assert.Equal(UserAccessManagementService.GrantAccessResult.Granted, result);
        using var verify = CreateContext();
        var access = verify.UserAccesses.Include(u => u.ScopedGroups).Single();
        Assert.Equal("person@example.com", access.Email);
        Assert.Empty(access.ScopedGroups); // non-scopable role: the group ID passed in is ignored.
    }

    [Fact]
    public async Task GrantAccessAsync_GrantsAScopableRoleWithTheGivenGroups()
    {
        using var context = CreateContext();
        var roleId = SeedRole(context, "Viewer", isScopable: true, Permission.DomainsView);
        context.Groups.Add(new Group { Name = "Client A" });
        context.SaveChanges();
        var groupId = context.Groups.Single().Id;

        var result = await UserAccessManagementService.GrantAccessAsync(context, TestActors.Admin, "client@example.com", roleId, [groupId], CancellationToken.None);

        Assert.Equal(UserAccessManagementService.GrantAccessResult.Granted, result);
        using var verify = CreateContext();
        var access = verify.UserAccesses.Include(u => u.ScopedGroups).Single();
        Assert.Single(access.ScopedGroups);
    }

    [Fact]
    public async Task GrantAccessAsync_RejectsADuplicateEmail()
    {
        using var context = CreateContext();
        var roleId = SeedRole(context, "Viewer", isScopable: true, Permission.DomainsView);
        await UserAccessManagementService.GrantAccessAsync(context, TestActors.Admin, "person@example.com", roleId, [], CancellationToken.None);

        var result = await UserAccessManagementService.GrantAccessAsync(context, TestActors.Admin, "person@example.com", roleId, [], CancellationToken.None);

        Assert.Equal(UserAccessManagementService.GrantAccessResult.AlreadyExists, result);
    }

    [Fact]
    public async Task GrantAccessAsync_RejectsAnUnknownRole()
    {
        using var context = CreateContext();

        var result = await UserAccessManagementService.GrantAccessAsync(context, TestActors.Admin, "person@example.com", 999, [], CancellationToken.None);

        Assert.Equal(UserAccessManagementService.GrantAccessResult.RoleNotFound, result);
    }

    [Fact]
    public async Task UpdateAccessAsync_ClearsScopedGroups_WhenNewRoleIsNotScopable()
    {
        using var context = CreateContext();
        var viewerRoleId = SeedRole(context, "Viewer", isScopable: true, Permission.DomainsView);
        var managerRoleId = SeedRole(context, "Domain Manager", isScopable: false, Permission.DomainsEdit);
        context.Groups.Add(new Group { Name = "Client A" });
        context.SaveChanges();
        var groupId = context.Groups.Single().Id;
        await UserAccessManagementService.GrantAccessAsync(context, TestActors.Admin, "person@example.com", viewerRoleId, [groupId], CancellationToken.None);
        var accessId = context.UserAccesses.Single().Id;

        await UserAccessManagementService.UpdateAccessAsync(context, TestActors.Admin, accessId, managerRoleId, [groupId], CancellationToken.None);

        using var verify = CreateContext();
        var access = verify.UserAccesses.Include(u => u.ScopedGroups).Single();
        Assert.Equal(managerRoleId, access.RoleId);
        Assert.Empty(access.ScopedGroups);
    }

    [Fact]
    public async Task RevokeAccessAsync_RemovesTheGrant()
    {
        using var context = CreateContext();
        var roleId = SeedRole(context, "Viewer", isScopable: true, Permission.DomainsView);
        await UserAccessManagementService.GrantAccessAsync(context, TestActors.Admin, "person@example.com", roleId, [], CancellationToken.None);
        var accessId = context.UserAccesses.Single().Id;

        var result = await UserAccessManagementService.RevokeAccessAsync(context, TestActors.Admin, accessId, CancellationToken.None);

        Assert.Equal(UserAccessManagementService.RevokeAccessResult.Revoked, result);
        using var verify = CreateContext();
        Assert.Empty(verify.UserAccesses);
    }

    [Fact]
    public async Task RevokeAccessAsync_RefusesToRemoveTheLastGrantWithAccessManage()
    {
        using var context = CreateContext();
        var adminRoleId = SeedRole(context, "Admin", isScopable: false, Permission.AccessManage);
        await UserAccessManagementService.GrantAccessAsync(context, TestActors.Admin, "admin@example.com", adminRoleId, [], CancellationToken.None);
        var accessId = context.UserAccesses.Single().Id;

        var result = await UserAccessManagementService.RevokeAccessAsync(context, TestActors.Admin, accessId, CancellationToken.None);

        Assert.Equal(UserAccessManagementService.RevokeAccessResult.LastAdminGuard, result);
        using var verify = CreateContext();
        Assert.Single(verify.UserAccesses); // not removed.
    }

    [Fact]
    public async Task RevokeAccessAsync_AllowsRemovingAnAccessManageGrant_WhenAnotherOneRemains()
    {
        using var context = CreateContext();
        var adminRoleId = SeedRole(context, "Admin", isScopable: false, Permission.AccessManage);
        await UserAccessManagementService.GrantAccessAsync(context, TestActors.Admin, "admin1@example.com", adminRoleId, [], CancellationToken.None);
        await UserAccessManagementService.GrantAccessAsync(context, TestActors.Admin, "admin2@example.com", adminRoleId, [], CancellationToken.None);
        var firstAccessId = context.UserAccesses.Single(u => u.Email == "admin1@example.com").Id;

        var result = await UserAccessManagementService.RevokeAccessAsync(context, TestActors.Admin, firstAccessId, CancellationToken.None);

        Assert.Equal(UserAccessManagementService.RevokeAccessResult.Revoked, result);
        using var verify = CreateContext();
        var remaining = verify.UserAccesses.Single();
        Assert.Equal("admin2@example.com", remaining.Email);
    }

    [Fact]
    public async Task RevokeAccessAsync_AllowsRemovingANonAccessManageGrant_EvenAsTheOnlyGrant()
    {
        using var context = CreateContext();
        var roleId = SeedRole(context, "Viewer", isScopable: true, Permission.DomainsView);
        await UserAccessManagementService.GrantAccessAsync(context, TestActors.Admin, "person@example.com", roleId, [], CancellationToken.None);
        var accessId = context.UserAccesses.Single().Id;

        var result = await UserAccessManagementService.RevokeAccessAsync(context, TestActors.Admin, accessId, CancellationToken.None);

        Assert.Equal(UserAccessManagementService.RevokeAccessResult.Revoked, result);
        using var verify = CreateContext();
        Assert.Empty(verify.UserAccesses);
    }

    [Fact]
    public async Task ResolveAsync_BindsAPendingGrant_ByEmailOnFirstSignIn()
    {
        using var context = CreateContext();
        var roleId = SeedRole(context, "Viewer", isScopable: true, Permission.DomainsView);
        await UserAccessManagementService.GrantAccessAsync(context, TestActors.Admin, "person@example.com", roleId, [], CancellationToken.None);

        var resolved = await UserAccessManagementService.ResolveAsync(context, "oid-123", "person@example.com", CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("oid-123", resolved!.EntraObjectId);
        using var verify = CreateContext();
        Assert.Equal("oid-123", verify.UserAccesses.Single().EntraObjectId);
    }

    [Fact]
    public async Task ResolveAsync_FindsAnAlreadyBoundGrant_ByObjectIdRegardlessOfCurrentEmail()
    {
        using var context = CreateContext();
        var roleId = SeedRole(context, "Viewer", isScopable: true, Permission.DomainsView);
        await UserAccessManagementService.GrantAccessAsync(context, TestActors.Admin, "person@example.com", roleId, [], CancellationToken.None);
        await UserAccessManagementService.ResolveAsync(context, "oid-123", "person@example.com", CancellationToken.None);

        var resolved = await UserAccessManagementService.ResolveAsync(context, "oid-123", "person-renamed@example.com", CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("person@example.com", resolved!.Email); // unchanged - lookup used the object ID, not the new email.
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNull_WhenNeitherObjectIdNorEmailMatchesAnyGrant()
    {
        using var context = CreateContext();

        var resolved = await UserAccessManagementService.ResolveAsync(context, "oid-999", "nobody@example.com", CancellationToken.None);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task GrantAccessAsync_RecordsTheRoleAndGroups()
    {
        await using (var context = CreateContext())
        {
            context.Roles.Add(new Role { Name = "Client viewer", IsScopable = true, Permissions = [Permission.DomainsView] });
            context.Groups.Add(new Group { Name = "Client A" });
            await context.SaveChangesAsync();
            var roleId = (await context.Roles.SingleAsync(role => role.Name == "Client viewer")).Id;
            var groupId = (await context.Groups.SingleAsync()).Id;
            await UserAccessManagementService.GrantAccessAsync(context, TestActors.Admin, "sam@contoso.com", roleId, [groupId]);
        }

        await using var verify = CreateContext();
        var access = await verify.UserAccesses.SingleAsync(userAccess => userAccess.Email == "sam@contoso.com");
        var entry = await verify.AuditEntries.SingleAsync(auditEntry => auditEntry.Action == AuditActions.AccessGranted);
        Assert.Equal((access.Id.ToString(), "sam@contoso.com", "Granted sam@contoso.com the Client viewer role"), (entry.TargetId, entry.TargetName, entry.Summary));
        Assert.Equal([new AuditFieldChange("Role", null, "Client viewer"), new AuditFieldChange("Groups", "None", "Client A")], entry.Changes);
    }

    [Fact]
    public async Task RevokeAccessAsync_RecordsNothing_WhenItWouldRemoveTheLastAdmin()
    {
        await using (var context = CreateContext())
        {
            context.Roles.Add(new Role { Name = "Admin", IsLocked = true, Permissions = [Permission.AccessManage] });
            await context.SaveChangesAsync();
            var roleId = (await context.Roles.SingleAsync()).Id;
            await UserAccessManagementService.GrantAccessAsync(context, TestActors.Admin, "only-admin@contoso.com", roleId, []);
            var accessId = (await context.UserAccesses.SingleAsync()).Id;

            var result = await UserAccessManagementService.RevokeAccessAsync(context, TestActors.Admin, accessId);

            Assert.Equal(UserAccessManagementService.RevokeAccessResult.LastAdminGuard, result);
        }

        await using var verify = CreateContext();
        Assert.DoesNotContain(verify.AuditEntries, entry => entry.Action == AuditActions.AccessRevoked);
    }

    [Fact]
    public async Task SetClientPortalAsync_OnAScopedGrant_TurnsItOnAndAudits()
    {
        await using var context = CreateContext();
        var viewerRole = new Role { Name = "Client viewer", IsScopable = true, Permissions = [Permission.DomainsView] };
        var access = new UserAccess { Email = "client@aurora.example", Role = viewerRole, ScopedGroups = [new Group { Name = "Aurora Retail" }] };
        context.UserAccesses.Add(access);
        await context.SaveChangesAsync();

        var result = await UserAccessManagementService.SetClientPortalAsync(context, TestActors.Admin, access.Id, true);

        Assert.Equal(UserAccessManagementService.SetClientPortalResult.Updated, result);
        await using var verify = CreateContext();
        Assert.True((await verify.UserAccesses.SingleAsync()).IsClientPortal);
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.AccessClientPortalChanged, entry.Action);
        Assert.Contains(entry.Changes, change => change.Field == "Client portal" && change.New == "Yes");
    }

    [Fact]
    public async Task SetClientPortalAsync_OnAnUnscopedGrant_IsRefused()
    {
        await using var context = CreateContext();
        var viewerRole = new Role { Name = "Client viewer", IsScopable = true, Permissions = [Permission.DomainsView] };
        var access = new UserAccess { Email = "client@aurora.example", Role = viewerRole };
        context.UserAccesses.Add(access);
        await context.SaveChangesAsync();

        var result = await UserAccessManagementService.SetClientPortalAsync(context, TestActors.Admin, access.Id, true);

        Assert.Equal(UserAccessManagementService.SetClientPortalResult.NeedsScopedGroups, result);
        await using var verify = CreateContext();
        Assert.False((await verify.UserAccesses.SingleAsync()).IsClientPortal);
        Assert.Empty(verify.AuditEntries);
    }

    [Fact]
    public async Task UpdateAccessAsync_RemovingAPortalGrantsLastGroup_IsRefused()
    {
        await using var context = CreateContext();
        var viewerRole = new Role { Name = "Client viewer", IsScopable = true, Permissions = [Permission.DomainsView] };
        var access = new UserAccess { Email = "client@aurora.example", Role = viewerRole, ScopedGroups = [new Group { Name = "Aurora Retail" }], IsClientPortal = true };
        context.UserAccesses.Add(access);
        await context.SaveChangesAsync();

        var result = await UserAccessManagementService.UpdateAccessAsync(context, TestActors.Admin, access.Id, viewerRole.Id, []);

        Assert.Equal(UserAccessManagementService.UpdateAccessResult.ClientPortalNeedsGroups, result);
        await using var verify = CreateContext();
        Assert.Single((await verify.UserAccesses.Include(grant => grant.ScopedGroups).SingleAsync()).ScopedGroups);
    }
}
