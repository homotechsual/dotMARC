using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Psa;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class PsaConnectionAlertTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public PsaConnectionAlertTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private AlertingService Alerting() =>
        new(new FakeDbContextFactory(_connectionString), new FakeAlertWebhookClient(), PsaTestSupport.ForHalo(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);

    [Fact]
    public async Task ALostConnection_RaisesOneAlert_ThatResolves()
    {
        var alerting = Alerting();

        await alerting.RaisePsaConnectionLostAsync(PsaKind.NinjaOne, "NinjaOne refused the sign-in (400): invalid_grant");
        await alerting.RaisePsaConnectionLostAsync(PsaKind.NinjaOne, "NinjaOne refused the sign-in (400): invalid_grant");

        await using (var verify = CreateContext())
        {
            var alert = await verify.AlertEvents.SingleAsync();
            Assert.Equal((AlertTypes.PsaConnectionLost, "NinjaOne connection", "PSA connection lost", false),
                (alert.AlertType, alert.DomainName, alert.Title, alert.IsResolved));
            Assert.Contains("invalid_grant", alert.Message);
            Assert.Contains("Manage > Alerts & PSA > PSA > NinjaOne", alert.Message);
        }

        await alerting.ResolvePsaConnectionLostAsync(PsaKind.NinjaOne);

        await using var afterResolve = CreateContext();
        Assert.True((await afterResolve.AlertEvents.SingleAsync()).IsResolved);
    }

    [Fact]
    public void TheAlertType_DoesNotRaiseTicketsByDefault()
    {
        Assert.False(AlertTypes.Find(AlertTypes.PsaConnectionLost)!.CreatesTicketByDefault);
    }
}
