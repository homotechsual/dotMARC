using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>The single Delivery mode setting became a switch per channel; an upgrade must keep sending where it did.</summary>
[Collection("Postgres")]
public sealed class AlertChannelMigrationTests(PostgresContainerFixture fixture)
{
    private const string MigrationBeforeChannelSwitches = "20261005171738_AddApiKeyNameCaseInsensitiveIndex";

    [Theory]
    [InlineData("Teams", true, false)]
    [InlineData("Generic", false, true)]
    [InlineData("Both", true, true)]
    public async Task TheOldDeliveryMode_BecomesTheMatchingChannelSwitches(string deliveryMode, bool teams, bool generic)
    {
        var (connectionString, cleanup) = await fixture.CreateDatabaseAsync();
        await using (cleanup)
        {
            await using (var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connectionString).Options))
            {
                await context.GetService<IMigrator>().MigrateAsync(MigrationBeforeChannelSwitches);
                await context.Database.ExecuteSqlRawAsync("UPDATE \"NotificationSettings\" SET \"DeliveryMode\" = {0}", deliveryMode);
                await context.Database.MigrateAsync();
            }

            await using var verify = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connectionString).Options);
            var settings = await verify.NotificationSettings.SingleAsync();
            Assert.Equal(teams, settings.TeamsEnabled);
            Assert.Equal(generic, settings.GenericWebhookEnabled);
            Assert.False(settings.SlackEnabled);
        }
    }
}
