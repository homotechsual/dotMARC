using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Security;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class ApiKeyManagementServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ApiKeyManagementServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private async Task<int> SeedRoleAsync(string name, bool isScopable, params Permission[] permissions)
    {
        await using var context = CreateContext();
        var role = new Role { Name = name, IsScopable = isScopable, Permissions = [.. permissions] };
        context.Roles.Add(role);
        await context.SaveChangesAsync();
        return role.Id;
    }

    private async Task<int> SeedGroupAsync(string name)
    {
        await using var context = CreateContext();
        var group = new Group { Name = name };
        context.Groups.Add(group);
        await context.SaveChangesAsync();
        return group.Id;
    }

    [Fact]
    public async Task Create_ReturnsTheSecretOnce_AndStoresOnlyItsHash()
    {
        var roleId = await SeedRoleAsync("Reader", isScopable: false, Permission.DomainsView);

        CreateApiKeyResult result;
        await using (var context = CreateContext())
        {
            result = await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, "  Halo sync  ", roleId, [], 90);
        }

        Assert.Null(result.Error);
        Assert.StartsWith("dmk_", result.Secret);
        await using var verify = CreateContext();
        var stored = await verify.ApiKeys.SingleAsync();
        Assert.Equal("Halo sync", stored.Name);
        Assert.Equal(ApiKeySecrets.Hash(result.Secret!), stored.Hash);
        Assert.Equal(result.Secret![..12], stored.Prefix);
        Assert.DoesNotContain(result.Secret, stored.Hash);
        Assert.Equal("Test Admin", stored.CreatedBy);
        Assert.Equal(90, (int)Math.Round((stored.ExpiresUtc - stored.CreatedUtc).TotalDays));
    }

    [Fact]
    public async Task Create_RefusesARoleThatCanManageAccess()
    {
        var roleId = await SeedRoleAsync("Admins", isScopable: false, Permission.DomainsView, Permission.AccessManage);
        await using var context = CreateContext();

        var result = await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, "Too powerful", roleId, [], 90);

        Assert.Equal(CreateApiKeyError.RoleCanManageAccess, result.Error);
        Assert.False(await context.ApiKeys.AnyAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(366)]
    public async Task Create_RefusesALifetimeThatIsntOffered(int lifetimeDays)
    {
        var roleId = await SeedRoleAsync("Reader", isScopable: false, Permission.DomainsView);
        await using var context = CreateContext();

        var result = await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, "Odd lifetime", roleId, [], lifetimeDays);

        Assert.Equal(CreateApiKeyError.InvalidLifetime, result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_RefusesABlankName(string name)
    {
        var roleId = await SeedRoleAsync("Reader", isScopable: false, Permission.DomainsView);
        await using var context = CreateContext();

        var result = await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, name, roleId, [], 90);

        Assert.Equal(CreateApiKeyError.InvalidName, result.Error);
    }

    [Fact]
    public async Task Create_RefusesAnActiveNameInAnyCase_ButAllowsReusingARevokedName()
    {
        var roleId = await SeedRoleAsync("Reader", isScopable: false, Permission.DomainsView);
        await using var context = CreateContext();
        var first = await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, "Reporting", roleId, [], 90);

        var duplicate = await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, "REPORTING", roleId, [], 90);
        await ApiKeyManagementService.RevokeAsync(context, TestActors.Admin, first.Key!.Id);
        var reused = await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, "Reporting", roleId, [], 90);

        Assert.Equal(CreateApiKeyError.NameInUse, duplicate.Error);
        Assert.Null(reused.Error);
    }

    [Fact]
    public async Task Create_KeepsGroupsOnlyForAScopableRole()
    {
        var scopableRoleId = await SeedRoleAsync("Client viewer", isScopable: true, Permission.DomainsView);
        var plainRoleId = await SeedRoleAsync("Reader", isScopable: false, Permission.DomainsView);
        var groupId = await SeedGroupAsync("Contoso");
        await using var context = CreateContext();

        var scoped = await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, "Scoped", scopableRoleId, [groupId], 90);
        var unscoped = await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, "Unscoped", plainRoleId, [groupId], 90);

        await using var verify = CreateContext();
        var keys = await verify.ApiKeys.Include(key => key.ScopedGroups).ToDictionaryAsync(key => key.Id);
        Assert.Equal([groupId], keys[scoped.Key!.Id].ScopedGroups.Select(group => group.Id));
        Assert.Empty(keys[unscoped.Key!.Id].ScopedGroups);
    }

    [Fact]
    public async Task CreateAndRevoke_AreAudited()
    {
        var roleId = await SeedRoleAsync("Reader", isScopable: false, Permission.DomainsView);
        await using var context = CreateContext();
        var created = await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, "Halo sync", roleId, [], 30);

        var revoked = await ApiKeyManagementService.RevokeAsync(context, TestActors.Admin, created.Key!.Id);
        var revokedAgain = await ApiKeyManagementService.RevokeAsync(context, TestActors.Admin, created.Key.Id);

        Assert.Equal(RevokeApiKeyResult.Revoked, revoked);
        Assert.Equal(RevokeApiKeyResult.AlreadyRevoked, revokedAgain);
        await using var verify = CreateContext();
        var key = await verify.ApiKeys.SingleAsync();
        Assert.NotNull(key.RevokedUtc);
        Assert.Equal("Test Admin", key.RevokedBy);
        var actions = await verify.AuditEntries.OrderBy(entry => entry.Id).Select(entry => entry.Action).ToListAsync();
        Assert.Equal([AuditActions.ApiKeyCreated, AuditActions.ApiKeyRevoked], actions);
        Assert.All(await verify.AuditEntries.ToListAsync(), entry => Assert.Equal("ApiKey", entry.TargetType));
    }

    [Fact]
    public async Task Revoke_AnUnknownKey_IsNotFound()
    {
        await using var context = CreateContext();

        Assert.Equal(RevokeApiKeyResult.NotFound, await ApiKeyManagementService.RevokeAsync(context, TestActors.Admin, 999));
    }
}
