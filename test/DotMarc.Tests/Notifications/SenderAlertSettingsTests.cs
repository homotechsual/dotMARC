using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>The sender alert settings arrive with sensible defaults, both on a new install and on a settings row that already exists.</summary>
[Collection("Postgres")]
public sealed class SenderAlertSettingsTests(PostgresContainerFixture fixture)
{
    private const string MigrationBeforeSenderAlertSettings = "20261118140752_AddSenderDecisions";

    private static DotMarcDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connectionString).Options);

    [Fact]
    public async Task ANewInstall_StartsWithTheSenderAlertDefaults()
    {
        var (connectionString, cleanup) = await fixture.CreateDatabaseAsync();
        await using (cleanup)
        {
            await using (var context = CreateContext(connectionString))
            {
                await context.Database.MigrateAsync();
            }

            await using var verify = CreateContext(connectionString);
            var settings = await verify.NotificationSettings.SingleAsync();
            Assert.Equal((true, 20, 7, true, 10),
                (settings.NewSenderAlertEnabled, settings.NewSenderMinVolume, settings.NewSenderDays,
                 settings.UnauthorisedDeliveredAlertEnabled, settings.UnauthorisedDeliveredMinVolume));
        }
    }

    [Fact]
    public async Task AnExistingSettingsRow_GetsTheDefaultsToo_AndKeepsItsOtherValues()
    {
        var (connectionString, cleanup) = await fixture.CreateDatabaseAsync();
        await using (cleanup)
        {
            await using (var context = CreateContext(connectionString))
            {
                await context.GetService<IMigrator>().MigrateAsync(MigrationBeforeSenderAlertSettings);
                await context.Database.ExecuteSqlRawAsync("UPDATE \"NotificationSettings\" SET \"MissingReportThresholdDays\" = 5");
                await context.Database.MigrateAsync();
            }

            await using var verify = CreateContext(connectionString);
            var settings = await verify.NotificationSettings.SingleAsync();
            Assert.Equal((true, 20, 7, true, 10, 5),
                (settings.NewSenderAlertEnabled, settings.NewSenderMinVolume, settings.NewSenderDays,
                 settings.UnauthorisedDeliveredAlertEnabled, settings.UnauthorisedDeliveredMinVolume, settings.MissingReportThresholdDays));
        }
    }
}
