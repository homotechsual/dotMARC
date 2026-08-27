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
}
