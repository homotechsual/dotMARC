using DotMarc.Data;
using DotMarc.Demo;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Demo;

/// <summary>The demo and the product agree: the real alerting cycle, run over the seeded demo, raises the sender alerts the demo shows
/// and no others, and the decisions land where the narrative says they do.</summary>
[Collection("Postgres")]
public sealed class DemoSenderStatesTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DemoSenderStatesTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private async Task SeedDemoAsync()
    {
        await using var context = CreateContext();
        await DemoDataSeeder.ResetAsync(context, DemoDataGenerator.Generate(new Random(7), DateTimeOffset.UtcNow), CancellationToken.None);
    }

    [Fact]
    public async Task TheSeededDecisions_LandOnTheRightOwners()
    {
        await SeedDemoAsync();

        await using var verify = CreateContext();
        var decisions = await verify.SenderDecisions.AsNoTracking().ToListAsync();
        var cobaltGroupId = (await verify.Groups.SingleAsync(group => group.Name == "Cobalt Freight")).Id;
        var emberwoodId = (await verify.Domains.SingleAsync(domain => domain.Name == "emberwood-finance.example")).Id;

        Assert.Equal(4, decisions.Count);
        Assert.Equal(2, decisions.Count(decision => decision.Scope == SenderDecisionScope.Baseline && decision.Status == SenderDecisionStatus.Authorised));
        Assert.Contains(decisions, decision => decision is { Scope: SenderDecisionScope.Group, Key: "Mailchimp", Status: SenderDecisionStatus.Unauthorised } && decision.GroupId == cobaltGroupId);
        Assert.Contains(decisions, decision => decision is { Scope: SenderDecisionScope.Domain, Key: "HubSpot", Status: SenderDecisionStatus.UnderReview } && decision.DomainId == emberwoodId);
        Assert.All(decisions, decision => Assert.Equal("Nova MSP admin", decision.DecidedByName));
    }

    [Fact]
    public async Task RunningTheRealAlertingCycleOverTheSeededDemo_RaisesNoSenderAlertTheDemoDoesNotShow_AndLosesNone()
    {
        await SeedDemoAsync();
        await using (var before = CreateContext())
        {
            Assert.Equal(2, await before.AlertEvents.CountAsync(alert => alert.AlertType == AlertTypes.NewUnreviewedSender || alert.AlertType == AlertTypes.UnauthorisedSenderDelivered));
        }

        var service = new AlertingService(new FakeDbContextFactory(_connectionString), new FakeAlertWebhookClient(), PsaTestSupport.ForHalo(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);
        await service.CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        var senderAlerts = await verify.AlertEvents
            .Where(alert => alert.AlertType == AlertTypes.NewUnreviewedSender || alert.AlertType == AlertTypes.UnauthorisedSenderDelivered)
            .OrderBy(alert => alert.AlertType).ToListAsync();

        Assert.Equal(
            [(AlertTypes.NewUnreviewedSender, "shop.aurora-retail.example", false), (AlertTypes.UnauthorisedSenderDelivered, "cobalt-freight.example", false)],
            senderAlerts.OrderBy(alert => alert.AlertType, StringComparer.Ordinal).Select(alert => (alert.AlertType, alert.DomainName, alert.IsResolved)));
    }
}
