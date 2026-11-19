using DotMarc.Data;
using DotMarc.Tests.Api;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class AlertRulesPermissionTests(PostgresContainerFixture fixture)
{
    private static DotMarcDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connectionString).Options);

    private static Task BootstrapAsync(DotMarcDbContext context) =>
        AccessBootstrapper.BootstrapWithLeaderLockAsync(context, Options.Create(new InitialAdminsOptions { Emails = "" }), NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task TheAdminRole_HasAlertRulesManage_AfterBootstrapping()
    {
        var (connectionString, cleanup) = await fixture.CreateDatabaseAsync();
        await using (cleanup)
        {
            await using (var context = CreateContext(connectionString))
            {
                await context.Database.MigrateAsync();
                await BootstrapAsync(context);
            }

            await using var verify = CreateContext(connectionString);
            Assert.Contains(Permission.AlertRulesManage, (await verify.Roles.SingleAsync(role => role.Name == "Admin")).Permissions);
        }
    }

    [Fact]
    public async Task ACustomRole_DoesNotGetItAutomatically()
    {
        var (connectionString, cleanup) = await fixture.CreateDatabaseAsync();
        await using (cleanup)
        {
            await using (var context = CreateContext(connectionString))
            {
                await context.Database.MigrateAsync();
                context.Roles.Add(new Role { Name = "Alert watcher", Permissions = [Permission.AlertsView, Permission.AlertsManage] });
                await context.SaveChangesAsync();
                await BootstrapAsync(context);
            }

            await using var verify = CreateContext(connectionString);
            var custom = await verify.Roles.SingleAsync(role => role.Name == "Alert watcher");
            Assert.DoesNotContain(Permission.AlertRulesManage, custom.Permissions);
        }
    }

    [Fact]
    public async Task ThePermissionHasAnAuthorizationPolicy()
    {
        await using var host = await ApiTestHost.StartAsync(fixture);

        var policy = await host.Factory.Services.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(nameof(Permission.AlertRulesManage));

        Assert.NotNull(policy);
    }
}
