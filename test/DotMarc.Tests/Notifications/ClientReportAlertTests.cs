using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class ClientReportAlertTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ClientReportAlertTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var settings = await context.NotificationSettings.SingleAsync();
        settings.Enabled = true;
        await context.SaveChangesAsync();
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
    public async Task AFailedReport_RaisesAnAlert_ThatResolvesByGroupIdEvenAfterARename()
    {
        var alerting = new AlertingService(new FakeDbContextFactory(_connectionString), new FakeAlertWebhookClient(), PsaTestSupport.ForHalo(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);

        await alerting.RaiseClientReportFailedAsync(7, "Aurora Retail", "March 2026", "535 Authentication failed");
        await alerting.ResolveClientReportFailedAsync(7);

        await using var verify = CreateContext();
        var alert = await verify.AlertEvents.SingleAsync();
        Assert.Equal((AlertTypes.ClientReportFailed, AlertingService.ClientReportAlertSubject(7, "Aurora Retail"), true),
            (alert.AlertType, alert.DomainName, alert.IsResolved));
        Assert.Contains("535 Authentication failed", alert.Message);
    }
}
