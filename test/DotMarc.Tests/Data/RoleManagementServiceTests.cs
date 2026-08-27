using System.Data.Common;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    [Fact]
    public async Task RemoveRoleAsync_ReturnsInUse_WhenAGrantIsInsertedBetweenTheCheckAndTheDelete()
    {
        using var seedContext = CreateContext();
        await RoleManagementService.AddRoleAsync(seedContext, "Domain Manager", [Permission.DomainsView], CancellationToken.None);
        var roleId = seedContext.Roles.Single().Id;

        // Reproduces the check-then-act race the FK-violation catch in RemoveRoleAsync guards
        // against: this interceptor inserts a conflicting UserAccess grant against the role
        // right before the DELETE statement is sent -- i.e. strictly after RemoveRoleAsync's
        // own "in use" AnyAsync pre-check has already run and found nothing. Without the fix
        // this insert causes SaveChangesAsync to throw an unhandled DbUpdateException wrapping
        // a PostgresException (SqlState 23503) instead of RemoveRoleAsync returning InUse.
        var interceptor = new DeleteRaceInterceptor(async () =>
        {
            using var racingContext = CreateContext();
            await UserAccessManagementService.GrantAccessAsync(racingContext, "person@example.com", roleId, [], CancellationToken.None);
        });
        var options = new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).AddInterceptors(interceptor).Options;
        using var context = new DotMarcDbContext(options);

        var result = await RoleManagementService.RemoveRoleAsync(context, roleId, CancellationToken.None);

        Assert.Equal(RoleManagementService.RemoveRoleResult.InUse, result);
        Assert.True(interceptor.Triggered, "the race interceptor never fired -- this test did not actually exercise the DELETE statement.");
        using var verify = CreateContext();
        Assert.NotEmpty(verify.Roles); // the role survives -- the delete did not go through.
        Assert.NotEmpty(verify.UserAccesses); // and the racing grant landed.
    }

    /// <summary>Fires <paramref name="onDelete"/> exactly once, synchronously ahead of the first
    /// DELETE command EF sends against the Roles table, so a test can deterministically insert a
    /// conflicting row in the exact window RemoveRoleAsync's InUse pre-check can't see -- without
    /// relying on real concurrent tasks racing each other (which would be flaky).</summary>
    private sealed class DeleteRaceInterceptor(Func<Task> onDelete) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }

        // Npgsql's EF Core provider sends a modification command like DELETE ... RETURNING via
        // ExecuteReader (not ExecuteNonQuery) so it can read back the affected-row count from
        // the RETURNING clause, so both interception points need to be handled here.
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await MaybeTriggerAsync(command).ConfigureAwait(false);
            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await MaybeTriggerAsync(command).ConfigureAwait(false);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
        }

        private async Task MaybeTriggerAsync(DbCommand command)
        {
            if (!Triggered && command.CommandText.Contains("DELETE", StringComparison.OrdinalIgnoreCase) && command.CommandText.Contains("Roles"))
            {
                Triggered = true;
                await onDelete().ConfigureAwait(false);
            }
        }
    }
}
