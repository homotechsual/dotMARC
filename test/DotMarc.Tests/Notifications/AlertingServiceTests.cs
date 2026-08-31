using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class AlertingServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AlertingServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    [Fact]
    public async Task CheckPinnedDomainsAsync_CreatesOneMissedReportAlert_PerDomainWithinCooldown()
    {
        await using var seedContext = CreateContext();
        seedContext.Domains.Add(new Domain
        {
            Name = "contoso.io",
            IsMonitored = true,
            FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-10),
            LastReportReceivedUtc = DateTimeOffset.UtcNow.AddDays(-3)
        });
        await seedContext.SaveChangesAsync();

        var fakeNotifier = new FakeAlertWebhookClient();
        var factory = new TestDbContextFactory(_connectionString);
        var service = new AlertingService(
            factory,
            fakeNotifier,
            Options.Create(new NotificationOptions
            {
                Enabled = true,
                DeliveryMode = "Teams",
                TeamsWebhookUrl = "https://example.test/webhook",
                MissingReportThresholdDays = 2,
                CooldownMinutes = 180
            }),
            NullLogger<AlertingService>.Instance);

        await service.CheckPinnedDomainsAsync();
        await service.CheckPinnedDomainsAsync();

        await using var verifyContext = CreateContext();
        var alerts = await verifyContext.AlertEvents.OrderBy(e => e.CreatedUtc).ToListAsync();

        Assert.Single(alerts);
        Assert.Equal("MissedReport", alerts[0].AlertType);
        Assert.Equal("contoso.io", alerts[0].DomainName);
        Assert.Equal(1, fakeNotifier.CallCount);
    }

    private DotMarcDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DotMarcDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new DotMarcDbContext(options);
    }

    private sealed class TestDbContextFactory : IDbContextFactory<DotMarcDbContext>
    {
        private readonly string _connectionString;

        public TestDbContextFactory(string connectionString) => _connectionString = connectionString;

        public DotMarcDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<DotMarcDbContext>()
                .UseNpgsql(_connectionString)
                .Options;
            return new DotMarcDbContext(options);
        }

        public ValueTask<DotMarcDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => new(CreateDbContext());
    }

    private sealed class FakeAlertWebhookClient : IAlertWebhookClient
    {
        public int CallCount { get; private set; }

        public Task SendAlertAsync(string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }
}
