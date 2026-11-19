using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>The three fixed alert channels become destinations on upgrade, so an install keeps sending to the same places.</summary>
[Collection("Postgres")]
public sealed class AlertDestinationMigrationTests(PostgresContainerFixture fixture)
{
    private const string MigrationBeforeDestinations = "20261119112547_AddAlertRules";

    private static DotMarcDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connectionString).Options);

    [Fact]
    public async Task EachChannelWithAUrl_BecomesADestination_AndExistingRulesSendToAllAndFollowTheTicketRules()
    {
        var (connectionString, cleanup) = await fixture.CreateDatabaseAsync();
        await using (cleanup)
        {
            await using (var context = CreateContext(connectionString))
            {
                await context.GetService<IMigrator>().MigrateAsync(MigrationBeforeDestinations);
                await context.Database.ExecuteSqlRawAsync(
                    "UPDATE \"NotificationSettings\" SET \"TeamsEnabled\" = true, \"TeamsWebhookUrl\" = 'https://example.test/teams', " +
                    "\"SlackEnabled\" = false, \"SlackWebhookUrl\" = 'https://hooks.slack.com/services/T0/B0/secret', " +
                    "\"GenericWebhookEnabled\" = true, \"GenericWebhookUrl\" = NULL");
                await context.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"AlertRules\" (\"Kind\", \"Name\", \"Enabled\", \"Severity\", \"Position\", \"StopProcessing\") VALUES ('MissedReport', 'Before', true, 'Warning', 1, true)");
                await context.Database.MigrateAsync();
            }

            await using var verify = CreateContext(connectionString);
            var destinations = await verify.AlertDestinations.AsNoTracking().OrderBy(destination => destination.Name).ToListAsync();
            Assert.Collection(destinations,
                slack => Assert.Equal(("Slack", AlertChannel.Slack, "https://hooks.slack.com/services/T0/B0/secret", false), (slack.Name, slack.Kind, slack.Url, slack.Enabled)),
                teams => Assert.Equal(("Teams", AlertChannel.Teams, "https://example.test/teams", true), (teams.Name, teams.Kind, teams.Url, teams.Enabled)));

            var rule = await verify.AlertRules.AsNoTracking().SingleAsync();
            Assert.Equal((AlertDestinationMode.All, AlertTicketMode.FollowRules), (rule.DestinationMode, rule.TicketMode));
        }
    }

    [Fact]
    public async Task AGenericChannelWithAUrl_IsNamedGenericWebhook()
    {
        var (connectionString, cleanup) = await fixture.CreateDatabaseAsync();
        await using (cleanup)
        {
            await using (var context = CreateContext(connectionString))
            {
                await context.GetService<IMigrator>().MigrateAsync(MigrationBeforeDestinations);
                await context.Database.ExecuteSqlRawAsync(
                    "UPDATE \"NotificationSettings\" SET \"TeamsWebhookUrl\" = NULL, \"GenericWebhookEnabled\" = true, \"GenericWebhookUrl\" = 'https://example.test/generic'");
                await context.Database.MigrateAsync();
            }

            await using var verify = CreateContext(connectionString);
            var destination = await verify.AlertDestinations.AsNoTracking().SingleAsync();
            Assert.Equal(("Generic webhook", AlertChannel.GenericWebhook, true), (destination.Name, destination.Kind, destination.Enabled));
        }
    }
}
