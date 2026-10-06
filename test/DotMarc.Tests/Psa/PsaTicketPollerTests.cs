using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Psa;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Psa;

[Collection("Postgres")]
public sealed class PsaTicketPollerTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public PsaTicketPollerTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private PsaTicketPoller CreatePoller(params FakePsaProvider[] providers) =>
        new(new FakeDbContextFactory(_connectionString), providers,
            new PsaTicketService(providers, NullLogger<PsaTicketService>.Instance),
            new ConfigurationBuilder().Build(), _clock, NullLogger<PsaTicketPoller>.Instance);

    private async Task<int> SeedTicketAsync(PsaKind psa, string ticketId, bool alertResolved)
    {
        await using var context = CreateContext();
        var alert = new AlertEvent
        {
            DomainName = $"{ticketId}.example", AlertType = AlertTypes.MissedReport, Severity = "Warning", Title = "t", Message = "m", IsResolved = alertResolved,
            Tickets = [new AlertTicket { Psa = psa, TicketId = ticketId, IsOpen = true }],
        };
        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync();
        return alert.Id;
    }

    [Fact]
    public async Task ATicketClosedInThePsa_ResolvesItsAlert_AndIsAudited()
    {
        var alertId = await SeedTicketAsync(PsaKind.ConnectWise, "250", alertResolved: false);
        var connectWise = new FakePsaProvider(PsaKind.ConnectWise) { States = { ["250"] = PsaTicketState.Closed } };

        await CreatePoller(connectWise).PollOnceAsync(CancellationToken.None);

        await using var verify = CreateContext();
        Assert.True((await verify.AlertEvents.SingleAsync(alert => alert.Id == alertId)).IsResolved);
        Assert.False((await verify.AlertTickets.SingleAsync()).IsOpen);
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.AlertResolvedByTicket, entry.Action);
        Assert.Contains("ConnectWise", entry.Summary);
    }

    [Fact]
    public async Task ResolvingFromAClosedTicket_DoesntTryToCloseThatSameTicketAgain()
    {
        await SeedTicketAsync(PsaKind.ConnectWise, "250", alertResolved: false);
        var connectWise = new FakePsaProvider(PsaKind.ConnectWise) { States = { ["250"] = PsaTicketState.Closed } };

        await CreatePoller(connectWise).PollOnceAsync(CancellationToken.None);

        Assert.Empty(connectWise.Closed);
    }

    [Fact]
    public async Task ATicketDeletedInThePsa_StopsBeingChecked_ButLeavesTheAlertOpen()
    {
        var alertId = await SeedTicketAsync(PsaKind.Autotask, "3001", alertResolved: false);

        await CreatePoller(new FakePsaProvider(PsaKind.Autotask)).PollOnceAsync(CancellationToken.None);

        await using var verify = CreateContext();
        Assert.False((await verify.AlertEvents.SingleAsync(alert => alert.Id == alertId)).IsResolved);
        Assert.False((await verify.AlertTickets.SingleAsync()).IsOpen);
    }

    [Fact]
    public async Task AStillOpenTicket_IsStampedAsChecked()
    {
        await SeedTicketAsync(PsaKind.HaloPsa, "100", alertResolved: false);
        var halo = new FakePsaProvider(PsaKind.HaloPsa) { States = { ["100"] = PsaTicketState.Open } };

        await CreatePoller(halo).PollOnceAsync(CancellationToken.None);

        await using var verify = CreateContext();
        var ticket = await verify.AlertTickets.SingleAsync();
        Assert.True(ticket.IsOpen);
        Assert.Equal(_clock.GetUtcNow(), ticket.LastCheckedUtc);
    }

    [Fact]
    public async Task AResolvedAlertsOpenTicket_IsClosedAgain()
    {
        await SeedTicketAsync(PsaKind.ConnectWise, "250", alertResolved: true);
        var connectWise = new FakePsaProvider(PsaKind.ConnectWise) { States = { ["250"] = PsaTicketState.Open } };

        await CreatePoller(connectWise).PollOnceAsync(CancellationToken.None);

        Assert.Equal(["250"], connectWise.Closed);
        await using var verify = CreateContext();
        Assert.False((await verify.AlertTickets.SingleAsync()).IsOpen);
    }

    [Fact]
    public async Task AResolvedAlertsTicketThatIsAlreadyClosedOrGone_IsMarkedClosedWithoutClosingAgain()
    {
        // A close that failed is retried, but if a tech closed or deleted the ticket in the meantime there is nothing
        // to retry: closing a deleted ticket fails for ever, and closing a closed one adds another note each time.
        await SeedTicketAsync(PsaKind.ConnectWise, "250", alertResolved: true);
        await SeedTicketAsync(PsaKind.ConnectWise, "251", alertResolved: true);
        var connectWise = new FakePsaProvider(PsaKind.ConnectWise) { States = { ["250"] = PsaTicketState.Closed } };

        await CreatePoller(connectWise).PollOnceAsync(CancellationToken.None);

        Assert.Empty(connectWise.Closed);
        await using var verify = CreateContext();
        Assert.All(await verify.AlertTickets.ToListAsync(), ticket => Assert.False(ticket.IsOpen));
    }

    [Fact]
    public async Task OneTicketThatCantBeRead_DoesntStopTheOthersInTheSamePsa()
    {
        await SeedTicketAsync(PsaKind.ConnectWise, "250", alertResolved: false);
        var readableAlertId = await SeedTicketAsync(PsaKind.ConnectWise, "251", alertResolved: false);
        var connectWise = new FakePsaProvider(PsaKind.ConnectWise) { FailingTickets = { "250" }, States = { ["251"] = PsaTicketState.Closed } };
        var poller = CreatePoller(connectWise);

        await poller.PollOnceAsync(CancellationToken.None);

        await using (var verify = CreateContext())
        {
            Assert.True((await verify.AlertEvents.SingleAsync(alert => alert.Id == readableAlertId)).IsResolved);
        }

        // Some of its tickets worked, so the PSA isn't down and isn't backed off: the next cycle asks again.
        connectWise.FailingTickets.Clear();
        connectWise.States["250"] = PsaTicketState.Closed;
        await poller.PollOnceAsync(CancellationToken.None);

        await using var verifyAfter = CreateContext();
        Assert.All(await verifyAfter.AlertEvents.ToListAsync(), alert => Assert.True(alert.IsResolved));
    }

    [Fact]
    public async Task AResolvedAlertsTicketThatStillCantBeClosed_DoesntStopTheOthersInTheSamePsa()
    {
        await SeedTicketAsync(PsaKind.ConnectWise, "250", alertResolved: true);
        var readableAlertId = await SeedTicketAsync(PsaKind.ConnectWise, "251", alertResolved: false);
        var connectWise = new FakePsaProvider(PsaKind.ConnectWise) { FailingTickets = { "250" }, States = { ["251"] = PsaTicketState.Closed } };

        await CreatePoller(connectWise).PollOnceAsync(CancellationToken.None);

        await using var verify = CreateContext();
        Assert.True((await verify.AlertEvents.SingleAsync(alert => alert.Id == readableAlertId)).IsResolved);
        Assert.True((await verify.AlertTickets.SingleAsync(ticket => ticket.TicketId == "250")).IsOpen);
    }

    [Fact]
    public async Task OnePsaFailing_DoesntStopTheOthers()
    {
        await SeedTicketAsync(PsaKind.HaloPsa, "100", alertResolved: false);
        var connectWiseAlertId = await SeedTicketAsync(PsaKind.ConnectWise, "250", alertResolved: false);
        var halo = new FakePsaProvider(PsaKind.HaloPsa) { FailWith = new HttpRequestException("Halo is down") };
        var connectWise = new FakePsaProvider(PsaKind.ConnectWise) { States = { ["250"] = PsaTicketState.Closed } };

        await CreatePoller(halo, connectWise).PollOnceAsync(CancellationToken.None);

        await using var verify = CreateContext();
        Assert.True((await verify.AlertEvents.SingleAsync(alert => alert.Id == connectWiseAlertId)).IsResolved);
    }

    [Fact]
    public async Task AFailingPsa_BacksOff_AndIsAskedAgainOnceTheWaitIsOver()
    {
        await SeedTicketAsync(PsaKind.HaloPsa, "100", alertResolved: false);
        var halo = new FakePsaProvider(PsaKind.HaloPsa) { FailWith = new HttpRequestException("Halo is down") };
        var poller = CreatePoller(halo);

        await poller.PollOnceAsync(CancellationToken.None);   // fails: wait one interval (5 minutes) before the next try
        halo.FailWith = null;
        halo.States["100"] = PsaTicketState.Closed;
        await poller.PollOnceAsync(CancellationToken.None);   // still inside the wait: not asked

        await using (var verify = CreateContext())
        {
            Assert.False((await verify.AlertEvents.SingleAsync()).IsResolved);
        }

        _clock.Advance(TimeSpan.FromMinutes(5));
        await poller.PollOnceAsync(CancellationToken.None);

        await using var verifyAfter = CreateContext();
        Assert.True((await verifyAfter.AlertEvents.SingleAsync()).IsResolved);
    }

    [Fact]
    public async Task APsaThatIsntReady_IsLeftAlone()
    {
        await SeedTicketAsync(PsaKind.HaloPsa, "100", alertResolved: false);
        var halo = new FakePsaProvider(PsaKind.HaloPsa) { Ready = false, States = { ["100"] = PsaTicketState.Closed } };

        await CreatePoller(halo).PollOnceAsync(CancellationToken.None);

        await using var verify = CreateContext();
        Assert.True((await verify.AlertTickets.SingleAsync()).IsOpen);
    }
}
