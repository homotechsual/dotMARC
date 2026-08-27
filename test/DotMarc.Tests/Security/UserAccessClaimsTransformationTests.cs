using System.Security.Claims;
using DotMarc.Data;
using DotMarc.Security;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Security;

[Collection("Postgres")]
public sealed class UserAccessClaimsTransformationTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public UserAccessClaimsTransformationTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private FakeDbContextFactory CreateFactory() => new(_connectionString);

    private static ClaimsPrincipal PrincipalFor(string objectId, string email)
    {
        var identity = new ClaimsIdentity(
            [new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", objectId), new Claim("preferred_username", email)],
            authenticationType: "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal PrincipalForWithoutPreferredUsername(string objectId, string email)
    {
        // No "preferred_username" claim at all — only ClaimTypes.Email — to exercise the fallback
        // chain in UserAccessClaimsTransformation.TransformAsync. This assumption (that
        // preferred_username is the right claim to look at first) is unverifiable without a live
        // Entra sign-in, so the transformation falls back through ClaimTypes.Upn/Email/"email"
        // rather than relying on preferred_username alone.
        var identity = new ClaimsIdentity(
            [new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", objectId), new Claim(ClaimTypes.Email, email)],
            authenticationType: "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task TransformAsync_AddsAPermissionClaimPerGrantedPermission()
    {
        using (var context = CreateContext())
        {
            var role = new Role { Name = "Domain Manager", IsLocked = false, IsScopable = false, Permissions = [Permission.DomainsView, Permission.DomainsAdd] };
            context.Roles.Add(role);
            context.SaveChanges();
            await UserAccessManagementService.GrantAccessAsync(context, "person@example.com", role.Id, [], CancellationToken.None);
        }

        var transformation = new UserAccessClaimsTransformation(CreateFactory());
        var principal = await transformation.TransformAsync(PrincipalFor("oid-1", "person@example.com"));

        var permissionClaims = principal.FindAll(UserAccessClaimsTransformation.PermissionClaimType).Select(c => c.Value).ToList();
        Assert.Contains(nameof(Permission.DomainsView), permissionClaims);
        Assert.Contains(nameof(Permission.DomainsAdd), permissionClaims);
        Assert.DoesNotContain(nameof(Permission.DomainsDelete), permissionClaims);
    }

    [Fact]
    public async Task TransformAsync_AddsAScopedGroupClaimPerAccessibleGroup()
    {
        using (var context = CreateContext())
        {
            var role = new Role { Name = "Viewer", IsLocked = false, IsScopable = true, Permissions = [Permission.DomainsView] };
            var group = new Group { Name = "Client A" };
            context.Roles.Add(role);
            context.Groups.Add(group);
            context.SaveChanges();
            await UserAccessManagementService.GrantAccessAsync(context, "client@example.com", role.Id, [group.Id], CancellationToken.None);
        }

        var transformation = new UserAccessClaimsTransformation(CreateFactory());
        var principal = await transformation.TransformAsync(PrincipalFor("oid-2", "client@example.com"));

        var scopedGroupClaims = principal.FindAll(UserAccessClaimsTransformation.ScopedGroupClaimType).ToList();
        Assert.Single(scopedGroupClaims);
    }

    [Fact]
    public async Task TransformAsync_ResolvesByEmail_WhenPreferredUsernameClaimIsAbsentButClaimTypesEmailIsPresent()
    {
        using (var context = CreateContext())
        {
            var role = new Role { Name = "Domain Manager", IsLocked = false, IsScopable = false, Permissions = [Permission.DomainsView] };
            context.Roles.Add(role);
            context.SaveChanges();
            await UserAccessManagementService.GrantAccessAsync(context, "fallback@example.com", role.Id, [], CancellationToken.None);
        }

        var transformation = new UserAccessClaimsTransformation(CreateFactory());
        var principal = await transformation.TransformAsync(PrincipalForWithoutPreferredUsername("oid-fallback", "fallback@example.com"));

        var permissionClaims = principal.FindAll(UserAccessClaimsTransformation.PermissionClaimType).Select(c => c.Value).ToList();
        Assert.Contains(nameof(Permission.DomainsView), permissionClaims);
    }

    [Fact]
    public async Task TransformAsync_AddsNoClaims_ForAnUnrecognizedIdentity()
    {
        var transformation = new UserAccessClaimsTransformation(CreateFactory());

        var principal = await transformation.TransformAsync(PrincipalFor("oid-unknown", "nobody@example.com"));

        Assert.Empty(principal.FindAll(UserAccessClaimsTransformation.PermissionClaimType));
    }

    [Fact]
    public async Task TransformAsync_IsIdempotent_WhenCalledTwiceOnTheSamePrincipal()
    {
        using (var context = CreateContext())
        {
            var role = new Role { Name = "Domain Manager", IsLocked = false, IsScopable = false, Permissions = [Permission.DomainsView] };
            context.Roles.Add(role);
            context.SaveChanges();
            await UserAccessManagementService.GrantAccessAsync(context, "person@example.com", role.Id, [], CancellationToken.None);
        }

        var transformation = new UserAccessClaimsTransformation(CreateFactory());
        var principal = await transformation.TransformAsync(PrincipalFor("oid-3", "person@example.com"));
        principal = await transformation.TransformAsync(principal);

        Assert.Single(principal.FindAll(UserAccessClaimsTransformation.PermissionClaimType));
    }

    [Fact]
    public async Task TransformAsync_IsIdempotent_ForAScopedRoleWithNoPermissions()
    {
        // Regression test: a scopable Role with an empty Permissions list produces zero
        // PermissionClaimType claims but at least one ScopedGroupClaimType claim. The
        // idempotency guard must not infer "already transformed" from PermissionClaimType's
        // presence alone, or a second TransformAsync call re-resolves and duplicates the group
        // claim(s).
        using (var context = CreateContext())
        {
            var role = new Role { Name = "Scoped No-Permissions", IsLocked = false, IsScopable = true, Permissions = [] };
            var group = new Group { Name = "Client B" };
            context.Roles.Add(role);
            context.Groups.Add(group);
            context.SaveChanges();
            await UserAccessManagementService.GrantAccessAsync(context, "scoped-empty@example.com", role.Id, [group.Id], CancellationToken.None);
        }

        var transformation = new UserAccessClaimsTransformation(CreateFactory());
        var principal = await transformation.TransformAsync(PrincipalFor("oid-4", "scoped-empty@example.com"));
        principal = await transformation.TransformAsync(principal);

        Assert.Empty(principal.FindAll(UserAccessClaimsTransformation.PermissionClaimType));
        Assert.Single(principal.FindAll(UserAccessClaimsTransformation.ScopedGroupClaimType));
    }
}
