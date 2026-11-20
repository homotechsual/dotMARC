using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>Quiet hours and escalation arrive switched off, and alerts raised before them are never sent again.</summary>
[Collection("Postgres")]
public sealed class QuietHoursMigrationTests(PostgresContainerFixture fixture)
{
    private const string MigrationBeforeQuietHours = "20261119200122_AddAlertDestinations";

    [Fact]
    public async Task ExistingAlertsCountAsNotified_ExistingRulesHaveQuietHoursOff_AndTheTimeZoneIsUtc()
    {
        var (connectionString, cleanup) = await fixture.CreateDatabaseAsync();
        await using (cleanup)
        {
            DateTimeOffset createdUtc;
            await using (var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connectionString).Options))
            {
                await context.GetService<IMigrator>().MigrateAsync(MigrationBeforeQuietHours);
                await context.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"AlertRules\" (\"Kind\", \"Name\", \"Enabled\", \"Severity\", \"Position\", \"StopProcessing\") VALUES ('MissedReport', 'Before', true, 'Warning', 1, true)");
                await context.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"AlertEvents\" (\"DomainName\", \"AlertType\", \"Severity\", \"Title\", \"Message\", \"IsResolved\", \"CreatedUtc\") " +
                    "VALUES ('contoso.io', 'MissedReport', 'Warning', 'Missing report', 'm', false, now() - interval '3 days')");
                await context.Database.MigrateAsync();
            }

            await using var verify = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connectionString).Options);
            var alert = await verify.AlertEvents.AsNoTracking().SingleAsync();
            createdUtc = alert.CreatedUtc;
            Assert.Equal(createdUtc, alert.NotifiedUtc);
            Assert.Null(alert.EscalatedUtc);

            var rule = await verify.AlertRules.AsNoTracking().Include(candidate => candidate.EscalationDestinations).SingleAsync();
            Assert.Equal((AlertQuietMode.Off, true, (int?)null), (rule.QuietMode, rule.QuietIgnoredForCritical, rule.EscalateAfterMinutes));
            Assert.Null(rule.QuietStartMinutes);
            Assert.Null(rule.QuietEndMinutes);
            Assert.Empty(rule.EscalationDestinations);

            Assert.Equal("UTC", (await verify.NotificationSettings.AsNoTracking().SingleAsync()).TimeZoneId);
        }
    }
}
