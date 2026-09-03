// test/DotMarc.Tests/Notifications/PsaTicketServiceTests.cs
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class PsaTicketServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public PsaTicketServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private sealed class FakeHaloPsaClient : IHaloPsaClient
    {
        public int CreateCallCount { get; private set; }
        public int CloseCallCount { get; private set; }
        public string NextTicketId { get; set; } = "1000";

        public Task<IReadOnlyList<HaloClient>> ListClientsAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloClient>>([]);
        public Task<IReadOnlyList<HaloTicketType>> ListTicketTypesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloTicketType>>([]);
        public Task<IReadOnlyList<HaloTicketStatus>> ListStatusesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloTicketStatus>>([]);
        public Task<IReadOnlyList<HaloPriority>> ListPrioritiesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloPriority>>([]);

        public Task<string> CreateTicketAsync(HaloPsaSettings settings, int haloClientId, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default)
        {
            CreateCallCount++;
            return Task.FromResult(NextTicketId);
        }

        public Task CloseTicketAsync(HaloPsaSettings settings, string ticketId, string note, CancellationToken cancellationToken = default)
        {
            CloseCallCount++;
            return Task.CompletedTask;
        }
    }

    private async Task EnableHaloAsync()
    {
        await using var context = CreateContext();
        var settings = await context.HaloPsaSettings.SingleAsync();
        settings.Enabled = true;
        settings.AccountName = "contoso";
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task CreateTicketAsync_CreatesATicket_ForADomainInAMappedGroup()
    {
        await EnableHaloAsync();
        await using var context = CreateContext();
        var group = new Group { Name = "Client A", HaloClientId = 7 };
        context.Groups.Add(group);
        var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow, Groups = [group] };
        context.Domains.Add(domain);
        var alert = new AlertEvent { DomainName = "contoso.io", AlertType = "MissedReport", Severity = "Warning", Title = "t", Message = "m" };
        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync();

        var fakeClient = new FakeHaloPsaClient();
        var service = new PsaTicketService(fakeClient);
        await service.CreateTicketAsync(context, alert);
        await context.SaveChangesAsync();

        Assert.Equal(1, fakeClient.CreateCallCount);
        var saved = await context.AlertEvents.SingleAsync();
        Assert.Equal("HaloPSA", saved.ExternalTicketProvider);
        Assert.Equal("1000", saved.ExternalTicketId);
    }

    [Fact]
    public async Task CreateTicketAsync_SkipsTicketCreation_WhenAnEarlierUnresolvedAlertForTheSameDomainAndTypeAlreadyHasAnOpenTicket()
    {
        await EnableHaloAsync();
        await using var context = CreateContext();
        var group = new Group { Name = "Client A", HaloClientId = 7 };
        context.Groups.Add(group);
        var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow, Groups = [group] };
        context.Domains.Add(domain);

        // An earlier alert for the same domain+type, still unresolved, already has an open Halo
        // ticket — this is what a cooldown-driven re-fire of AlertingService.EnsureAlertAsync looks
        // like for a domain that's stayed unhealthy across multiple cooldown windows.
        var earlierAlert = new AlertEvent
        {
            DomainName = "contoso.io", AlertType = "MissedReport", Severity = "Warning", Title = "t", Message = "m",
            ExternalTicketProvider = "HaloPSA", ExternalTicketId = "1000"
        };
        context.AlertEvents.Add(earlierAlert);
        var newAlert = new AlertEvent { DomainName = "contoso.io", AlertType = "MissedReport", Severity = "Warning", Title = "t", Message = "m" };
        context.AlertEvents.Add(newAlert);
        await context.SaveChangesAsync();

        var fakeClient = new FakeHaloPsaClient();
        var service = new PsaTicketService(fakeClient);
        await service.CreateTicketAsync(context, newAlert);
        await context.SaveChangesAsync();

        Assert.Equal(0, fakeClient.CreateCallCount);
        var saved = await context.AlertEvents.SingleAsync(a => a.Id == newAlert.Id);
        Assert.Null(saved.ExternalTicketProvider);
        Assert.Null(saved.ExternalTicketId);
    }

    [Fact]
    public async Task CreateTicketAsync_DoesNothing_ForADomainWithNoMapping()
    {
        await EnableHaloAsync();
        await using var context = CreateContext();
        var domain = new Domain { Name = "unmapped.io", FirstSeenUtc = DateTimeOffset.UtcNow };
        context.Domains.Add(domain);
        var alert = new AlertEvent { DomainName = "unmapped.io", AlertType = "MissedReport", Severity = "Warning", Title = "t", Message = "m" };
        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync();

        var fakeClient = new FakeHaloPsaClient();
        var service = new PsaTicketService(fakeClient);
        await service.CreateTicketAsync(context, alert);

        Assert.Equal(0, fakeClient.CreateCallCount);
        Assert.Null((await context.AlertEvents.SingleAsync()).ExternalTicketId);
    }

    [Fact]
    public async Task CreateTicketAsync_DoesNothing_WhenHaloIsDisabled()
    {
        await using var context = CreateContext();
        var group = new Group { Name = "Client A", HaloClientId = 7 };
        context.Groups.Add(group);
        var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow, Groups = [group] };
        context.Domains.Add(domain);
        var alert = new AlertEvent { DomainName = "contoso.io", AlertType = "MissedReport", Severity = "Warning", Title = "t", Message = "m" };
        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync();

        var fakeClient = new FakeHaloPsaClient();
        var service = new PsaTicketService(fakeClient);
        await service.CreateTicketAsync(context, alert);

        Assert.Equal(0, fakeClient.CreateCallCount);
        Assert.Null((await context.AlertEvents.SingleAsync()).ExternalTicketId);
    }

    [Fact]
    public async Task CreateTicketAsync_DoesNothing_WhenTheDomainDoesNotExist()
    {
        await EnableHaloAsync();
        await using var context = CreateContext();
        var alert = new AlertEvent { DomainName = "missing.io", AlertType = "MissedReport", Severity = "Warning", Title = "t", Message = "m" };
        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync();

        var fakeClient = new FakeHaloPsaClient();
        var service = new PsaTicketService(fakeClient);
        await service.CreateTicketAsync(context, alert);

        Assert.Equal(0, fakeClient.CreateCallCount);
        Assert.Null((await context.AlertEvents.SingleAsync()).ExternalTicketId);
    }

    [Fact]
    public async Task CloseTicketAsync_ClosesTheTicket_WhenOneWasCreated()
    {
        await EnableHaloAsync();
        await using var context = CreateContext();
        var alert = new AlertEvent { DomainName = "contoso.io", AlertType = "MissedReport", Severity = "Warning", Title = "t", Message = "m", ExternalTicketProvider = "HaloPSA", ExternalTicketId = "4242" };
        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync();

        var fakeClient = new FakeHaloPsaClient();
        var service = new PsaTicketService(fakeClient);
        await service.CloseTicketAsync(context, alert);

        Assert.Equal(1, fakeClient.CloseCallCount);
    }

    [Fact]
    public async Task CloseTicketAsync_DoesNothing_WhenNoTicketWasCreated()
    {
        await using var context = CreateContext();
        var alert = new AlertEvent { DomainName = "contoso.io", AlertType = "MissedReport", Severity = "Warning", Title = "t", Message = "m" };
        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync();

        var fakeClient = new FakeHaloPsaClient();
        var service = new PsaTicketService(fakeClient);
        await service.CloseTicketAsync(context, alert);

        Assert.Equal(0, fakeClient.CloseCallCount);
    }
}
