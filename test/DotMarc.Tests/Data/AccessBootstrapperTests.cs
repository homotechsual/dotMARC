using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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

        await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, Options(""), NullLogger.Instance, CancellationToken.None);

        using var verify = CreateContext();
        var admin = verify.Roles.Single(r => r.Name == "Admin");
        Assert.True(admin.IsLocked);
        Assert.False(admin.IsScopable);
        Assert.Equal(Enum.GetValues<Permission>().Length, admin.Permissions.Count);

        var viewer = verify.Roles.Single(r => r.Name == "Viewer");
        Assert.False(viewer.IsLocked);
        Assert.True(viewer.IsScopable);
        Assert.Equal(AccessBootstrapper.ViewerPermissions, viewer.Permissions);
    }

    [Fact]
    public async Task BootstrapWithLeaderLockAsync_GrantsAdminToEachConfiguredEmail_WhenNoAccessExistsYet()
    {
        using var context = CreateContext();

        await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, Options(" first@example.com , second@example.com "), NullLogger.Instance, CancellationToken.None);

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
            await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, Options("existing@example.com"), NullLogger.Instance, CancellationToken.None);
        }

        using (var context = CreateContext())
        {
            await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, Options("someone-else@example.com"), NullLogger.Instance, CancellationToken.None);
        }

        using var verify = CreateContext();
        var grants = verify.UserAccesses.ToList();
        Assert.Single(grants);
        Assert.Equal("existing@example.com", grants[0].Email);
    }

    [Fact]
    public async Task BootstrapWithLeaderLockAsync_BackfillsNewPermissions_OntoAnAlreadyExistingAdminRole()
    {
        // Simulates a live deployment whose Admin role was created before a later Permission
        // enum value (e.g. MtaStsView) existed: EnsureBuiltInRoleAsync's "only set permissions
        // when creating the role" behavior otherwise leaves that Admin role permanently stuck
        // without the new permission, even though this class's own doc comment claims Admin
        // "self-syncs when the enum grows" — the two Enum.GetValues<Permission>() call sites
        // computing the same value doesn't help if neither one ever runs again for an existing
        // row.
        using (var context = CreateContext())
        {
            context.Roles.Add(new Role
            {
                Name = "Admin",
                IsLocked = true,
                IsScopable = false,
                Permissions = [Permission.DomainsView, Permission.DomainsAdd]
            });
            await context.SaveChangesAsync();
        }

        using (var context = CreateContext())
        {
            await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, Options(""), NullLogger.Instance, CancellationToken.None);
        }

        using var verify = CreateContext();
        var admin = verify.Roles.Single(r => r.Name == "Admin");
        Assert.Equal(Enum.GetValues<Permission>().Length, admin.Permissions.Count);
        Assert.Contains(Permission.MtaStsView, admin.Permissions);
        Assert.Contains(Permission.MtaStsManage, admin.Permissions);
    }

    [Fact]
    public async Task BootstrapWithLeaderLockAsync_DoesNotOverwrite_AnAdminEditedViewerRole()
    {
        // Viewer, unlike Admin, is IsLocked: false — deliberately editable via ManageAccess.razor.
        // Backfilling missing permissions onto Admin must not extend to silently overwriting a
        // Viewer role an admin has customized away from AccessBootstrapper.ViewerPermissions.
        using (var context = CreateContext())
        {
            context.Roles.Add(new Role
            {
                Name = "Viewer",
                IsLocked = false,
                IsScopable = true,
                Permissions = [Permission.DomainsView] // deliberately narrower than the canonical list
            });
            await context.SaveChangesAsync();
        }

        using (var context = CreateContext())
        {
            await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, Options(""), NullLogger.Instance, CancellationToken.None);
        }

        using var verify = CreateContext();
        var viewer = verify.Roles.Single(r => r.Name == "Viewer");
        Assert.Equal([Permission.DomainsView], viewer.Permissions);
    }

    [Fact]
    public async Task BootstrapWithLeaderLockAsync_IsIdempotentOnRoles_AcrossMultipleCalls()
    {
        using (var context = CreateContext())
        {
            await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, Options(""), NullLogger.Instance, CancellationToken.None);
        }

        using (var context = CreateContext())
        {
            await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, Options(""), NullLogger.Instance, CancellationToken.None);
        }

        using var verify = CreateContext();
        Assert.Equal(2, verify.Roles.Count()); // still exactly Admin + Viewer, not duplicated.
    }

    [Fact]
    public async Task BootstrapWithLeaderLockAsync_DeduplicatesCaseVariantEmails_InsteadOfCrashingOnTheUniqueIndex()
    {
        using var context = CreateContext();

        await AccessBootstrapper.BootstrapWithLeaderLockAsync(
            context, Options("dup@example.com,DUP@example.com, dup@example.com "), NullLogger.Instance, CancellationToken.None);

        using var verify = CreateContext();
        var grants = verify.UserAccesses.ToList();
        Assert.Single(grants); // deduplicated case-insensitively rather than throwing on the unique index.
        Assert.Equal("dup@example.com", grants[0].Email); // first occurrence's casing wins.
    }
}
