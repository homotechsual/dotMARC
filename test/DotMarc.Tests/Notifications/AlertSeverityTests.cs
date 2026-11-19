using System.Text.Json;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>Critical is a severity like the other two: it is stored on the alert and reaches every channel with its real value.</summary>
[Collection("Postgres")]
public sealed class AlertSeverityTests(PostgresContainerFixture fixture)
{
    [Theory]
    [InlineData("Info")]
    [InlineData("Warning")]
    [InlineData("Critical")]
    public async Task TheGenericWebhookPayload_CarriesTheAlertsSeverity(string severity)
    {
        var handler = new FakeHttpMessageHandler();
        var client = new GenericWebhookClient(new HttpClient(handler));

        await client.SendAlertAsync("https://example.test/generic", "contoso.io", "MissedReport", severity, "Missing report", "message", CancellationToken.None);

        using var body = JsonDocument.Parse(handler.RequestBodies.Single());
        Assert.Equal(severity, body.RootElement.GetProperty("severity").GetString());
    }

    [Fact]
    public async Task ACriticalAlert_IsPrefixedInTeams_AndAWarningIsNot()
    {
        var handler = new FakeHttpMessageHandler();
        var client = new TeamsWebhookClient(new HttpClient(handler));

        await client.SendAlertAsync("https://example.test/teams", "contoso.io", "MissedReport", "Critical", "Missing report", "message", CancellationToken.None);
        await client.SendAlertAsync("https://example.test/teams", "contoso.io", "MissedReport", "Warning", "Missing report", "message", CancellationToken.None);

        Assert.Contains("\"Critical: Missing report\"", handler.RequestBodies[0]);
        Assert.DoesNotContain("Critical:", handler.RequestBodies[1]);
    }

    [Fact]
    public async Task ACriticalAlert_IsPrefixedInSlack_AndAWarningIsNot()
    {
        var handler = new FakeHttpMessageHandler();
        var client = new SlackWebhookClient(new HttpClient(handler));

        await client.SendAlertAsync("https://hooks.slack.com/services/T000/B000/secret", "contoso.io", "MissedReport", "Critical", "Missing report", "message", CancellationToken.None);
        await client.SendAlertAsync("https://hooks.slack.com/services/T000/B000/secret", "contoso.io", "MissedReport", "Warning", "Missing report", "message", CancellationToken.None);

        using var critical = JsonDocument.Parse(handler.RequestBodies[0]);
        using var warning = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Contains("Critical: Missing report", critical.RootElement.GetProperty("text").GetString());
        Assert.DoesNotContain("Critical:", warning.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task AnAlertRaisedByACriticalRule_IsStoredAsCritical_AndSentAsCritical()
    {
        var (connectionString, cleanup) = await fixture.CreateDatabaseAsync();
        await using (cleanup)
        {
            var options = new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connectionString).Options;
            await using (var context = new DotMarcDbContext(options))
            {
                await context.Database.MigrateAsync();
                await NotificationSettingsService.SaveAsync(context, TestActors.Admin, new NotificationSettings
                {
                    Enabled = true, TeamsEnabled = true, TeamsWebhookUrl = "https://example.test/webhook"
                });
                await AlertRuleSeeder.EnsureDefaultPackAsync(context, CancellationToken.None);
                context.AlertRules.Add(new AlertRule { Kind = AlertTypes.MissedReport, Name = "Critical missing report", Severity = "Critical", Position = -10 });
                context.Domains.Add(new Domain { Name = "stale.test", IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-30), LastReportReceivedUtc = DateTimeOffset.UtcNow.AddDays(-5) });
                await context.SaveChangesAsync();
            }

            var notifier = new FakeAlertWebhookClient();
            var service = new AlertingService(new FakeDbContextFactory(connectionString), notifier, PsaTestSupport.ForHalo(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);

            await service.CheckPinnedDomainsAsync();

            await using var verify = new DotMarcDbContext(options);
            var alert = await verify.AlertEvents.SingleAsync(candidate => candidate.AlertType == AlertTypes.MissedReport);
            Assert.Equal("Critical", alert.Severity);
            Assert.Contains(notifier.Sent, sent => sent.AlertType == AlertTypes.MissedReport && sent.Severity == "Critical");
        }
    }
}
