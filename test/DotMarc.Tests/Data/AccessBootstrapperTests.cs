using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class AccessBootstrapperTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AccessBootstrapperTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static IOptions<InitialAdminsOptions> Options(string emails) =>
        Microsoft.Extensions.Options.Options.Create(new InitialAdminsOptions { Emails = emails });

    [Fact]
    public async Task BootstrapWithLeaderLockAsync_SeedsAdminAndViewerRoles()
    {
        using var context = CreateContext();

        await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, Options(""), CancellationToken.None);

        using var verify = CreateContext();
        var admin = verify.Roles.Single(r => r.Name == "Admin");
        Assert.True(admin.IsLocked);
        Assert.False(admin.IsScopable);
        Assert.Equal(Enum.GetValues<Permission>().Length, admin.Permissions.Count);

        var viewer = verify.Roles.Single(r => r.Name == "Viewer");
        Assert.False(viewer.IsLocked);
        Assert.True(viewer.IsScopable);
        Assert.Equal([Permission.DomainsView, Permission.GroupsView, Permission.TagsView], viewer.Permissions);
    }

    [Fact]
    public async Task BootstrapWithLeaderLockAsync_GrantsAdminToEachConfiguredEmail_WhenNoAccessExistsYet()
    {
        using var context = CreateContext();

        await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, Options(" first@example.com , second@example.com "), CancellationToken.None);

        using var verify = CreateContext();
        var adminRoleId = verify.Roles.Single(r => r.Name == "Admin").Id;
        var grants = verify.UserAccesses.ToList();
        Assert.Equal(2, grants.Count);
        Assert.All(grants, g => Assert.Equal(adminRoleId, g.RoleId));
        Assert.Contains(grants, g => g.Email == "first@example.com");
        Assert.Contains(grants, g => g.Email == "second@example.com");
    }

    [Fact]
    public async Task BootstrapWithLeaderLockAsync_DoesNothing_WhenAnyAccessAlreadyExists()
    {
        using (var context = CreateContext())
        {
            await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, Options("existing@example.com"), CancellationToken.None);
        }

        using (var context = CreateContext())
        {
            await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, Options("someone-else@example.com"), CancellationToken.None);
        }

        using var verify = CreateContext();
        var grants = verify.UserAccesses.ToList();
        Assert.Single(grants);
        Assert.Equal("existing@example.com", grants[0].Email);
    }

    [Fact]
    public async Task BootstrapWithLeaderLockAsync_IsIdempotentOnRoles_AcrossMultipleCalls()
    {
        using (var context = CreateContext())
        {
            await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, Options(""), CancellationToken.None);
        }

        using (var context = CreateContext())
        {
            await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, Options(""), CancellationToken.None);
        }

        using var verify = CreateContext();
        Assert.Equal(2, verify.Roles.Count()); // still exactly Admin + Viewer, not duplicated.
    }
}
