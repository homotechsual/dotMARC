// test/DotMarc.Tests/Notifications/PsaTicketServiceTests.cs
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Psa;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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

    private static PsaCompanyLink Link(PsaKind psa, string companyId) => new() { Psa = psa, CompanyId = companyId, CompanyName = $"Company {companyId}" };

    private static PsaTicketService CreateService(params FakePsaProvider[] providers) =>
        new(providers, NullLogger<PsaTicketService>.Instance);

    /// <summary>A domain in one Group linked in the given PSAs, and one new alert for it.</summary>
    private static async Task<AlertEvent> SeedAlertAsync(DotMarcDbContext context, params PsaCompanyLink[] groupLinks)
    {
        var group = new Group { Name = "Client A", PsaCompanyLinks = [.. groupLinks] };
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow, Groups = [group] });
        var alert = new AlertEvent { DomainName = "contoso.io", AlertType = AlertTypes.MissedReport, Severity = "Warning", Title = "t", Message = "m" };
        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync();
        return alert;
    }

    [Fact]
    public async Task CreateTicketsAsync_RaisesATicketInEveryReadyPsaTheDomainMapsTo()
    {
        await using var context = CreateContext();
        var alert = await SeedAlertAsync(context, Link(PsaKind.HaloPsa, "7"), Link(PsaKind.ConnectWise, "250"));
        var halo = new FakePsaProvider(PsaKind.HaloPsa);
        var connectWise = new FakePsaProvider(PsaKind.ConnectWise);
        var autotask = new FakePsaProvider(PsaKind.Autotask);

        await CreateService(halo, connectWise, autotask).CreateTicketsAsync(context, alert);
        await context.SaveChangesAsync();

        Assert.Equal("7", Assert.Single(halo.Created).CompanyId);
        Assert.Equal("250", Assert.Single(connectWise.Created).CompanyId);
        Assert.Empty(autotask.Created);
        var tickets = await context.AlertTickets.ToListAsync();
        Assert.All(tickets, ticket => Assert.True(ticket.IsOpen));
        Assert.Equal([PsaKind.HaloPsa, PsaKind.ConnectWise], tickets.Select(ticket => ticket.Psa).OrderBy(psa => psa));
    }

    [Fact]
    public async Task CreateTicketsAsync_SkipsAPsaThatIsntReady()
    {
        await using var context = CreateContext();
        var alert = await SeedAlertAsync(context, Link(PsaKind.HaloPsa, "7"));
        var halo = new FakePsaProvider(PsaKind.HaloPsa) { Ready = false };

        await CreateService(halo).CreateTicketsAsync(context, alert);
        await context.SaveChangesAsync();

        Assert.Empty(halo.Created);
        Assert.Empty(await context.AlertTickets.ToListAsync());
    }

    [Fact]
    public async Task CreateTicketsAsync_OnePsaFailing_DoesntStopTheOthers()
    {
        await using var context = CreateContext();
        var alert = await SeedAlertAsync(context, Link(PsaKind.HaloPsa, "7"), Link(PsaKind.ConnectWise, "250"));
        var halo = new FakePsaProvider(PsaKind.HaloPsa) { FailWith = new HttpRequestException("Halo is down") };
        var connectWise = new FakePsaProvider(PsaKind.ConnectWise);

        await CreateService(halo, connectWise).CreateTicketsAsync(context, alert);
        await context.SaveChangesAsync();

        Assert.Single(connectWise.Created);
        Assert.Equal(PsaKind.ConnectWise, (await context.AlertTickets.SingleAsync()).Psa);
    }

    [Fact]
    public async Task CreateTicketsAsync_AReRaisedAlert_OnlyGetsTicketsInPsasWithoutAnOpenOne()
    {
        await using var context = CreateContext();
        var earlier = await SeedAlertAsync(context, Link(PsaKind.HaloPsa, "7"), Link(PsaKind.ConnectWise, "250"));
        context.AlertTickets.Add(new AlertTicket { AlertEventId = earlier.Id, Psa = PsaKind.HaloPsa, TicketId = "900", IsOpen = true });
        var reRaised = new AlertEvent { DomainName = "contoso.io", AlertType = AlertTypes.MissedReport, Severity = "Warning", Title = "t", Message = "m" };
        context.AlertEvents.Add(reRaised);
        await context.SaveChangesAsync();
        var halo = new FakePsaProvider(PsaKind.HaloPsa);
        var connectWise = new FakePsaProvider(PsaKind.ConnectWise);

        await CreateService(halo, connectWise).CreateTicketsAsync(context, reRaised);
        await context.SaveChangesAsync();

        Assert.Empty(halo.Created);
        Assert.Single(connectWise.Created);
    }

    [Fact]
    public async Task CreateTicketsAsync_AResolvedEarlierAlertsTicket_DoesntBlockANewOne()
    {
        await using var context = CreateContext();
        var earlier = await SeedAlertAsync(context, Link(PsaKind.HaloPsa, "7"));
        earlier.IsResolved = true;
        context.AlertTickets.Add(new AlertTicket { AlertEventId = earlier.Id, Psa = PsaKind.HaloPsa, TicketId = "900", IsOpen = true });
        var later = new AlertEvent { DomainName = "contoso.io", AlertType = AlertTypes.MissedReport, Severity = "Warning", Title = "t", Message = "m" };
        context.AlertEvents.Add(later);
        await context.SaveChangesAsync();
        var halo = new FakePsaProvider(PsaKind.HaloPsa);

        await CreateService(halo).CreateTicketsAsync(context, later);

        Assert.Single(halo.Created);
    }

    [Fact]
    public async Task CreateTicketsAsync_FollowsTheTicketRules_ForEachPsasDecidingGroup()
    {
        await using var context = CreateContext();
        var alert = await SeedAlertAsync(context, Link(PsaKind.HaloPsa, "7"));
        var groupId = (await context.Groups.SingleAsync()).Id;
        context.AlertTicketRules.Add(new AlertTicketRule { AlertType = AlertTypes.MissedReport, GroupId = groupId, CreateTicket = false });
        await context.SaveChangesAsync();
        var halo = new FakePsaProvider(PsaKind.HaloPsa);

        await CreateService(halo).CreateTicketsAsync(context, alert);

        Assert.Empty(halo.Created);
    }

    [Fact]
    public async Task CreateTicketsAsync_UsesTheDomainsOwnLink_OverItsGroups()
    {
        await using var context = CreateContext();
        var alert = await SeedAlertAsync(context, Link(PsaKind.HaloPsa, "7"));
        var domain = await context.Domains.Include(candidate => candidate.PsaCompanyLinks).SingleAsync();
        domain.PsaCompanyLinks.Add(Link(PsaKind.HaloPsa, "99"));
        await context.SaveChangesAsync();
        var halo = new FakePsaProvider(PsaKind.HaloPsa);

        await CreateService(halo).CreateTicketsAsync(context, alert);

        Assert.Equal("99", Assert.Single(halo.Created).CompanyId);
    }

    [Fact]
    public async Task CloseTicketsAsync_ClosesEachOpenTicket_AndKeepsAFailedOneOpenForTheRetry()
    {
        await using var context = CreateContext();
        var alert = await SeedAlertAsync(context);
        context.AlertTickets.AddRange(
            new AlertTicket { AlertEventId = alert.Id, Psa = PsaKind.HaloPsa, TicketId = "1", IsOpen = true },
            new AlertTicket { AlertEventId = alert.Id, Psa = PsaKind.ConnectWise, TicketId = "2", IsOpen = true });
        await context.SaveChangesAsync();
        var halo = new FakePsaProvider(PsaKind.HaloPsa);
        var connectWise = new FakePsaProvider(PsaKind.ConnectWise) { FailWith = new HttpRequestException("ConnectWise is down") };

        var result = await CreateService(halo, connectWise).CloseTicketsAsync(context, alert);
        await context.SaveChangesAsync();

        Assert.Equal(new PsaCloseResult(1, 1), result);
        Assert.Equal(["1"], halo.Closed);
        var stillOpen = await context.AlertTickets.SingleAsync(ticket => ticket.IsOpen);
        Assert.Equal(PsaKind.ConnectWise, stillOpen.Psa);
    }

    [Fact]
    public async Task CloseTicketsAsync_ATicketInAPsaThatIsNoLongerRegistered_CountsAsFailed()
    {
        await using var context = CreateContext();
        var alert = await SeedAlertAsync(context);
        context.AlertTickets.Add(new AlertTicket { AlertEventId = alert.Id, Psa = PsaKind.Autotask, TicketId = "5", IsOpen = true });
        await context.SaveChangesAsync();

        var result = await CreateService(new FakePsaProvider(PsaKind.HaloPsa)).CloseTicketsAsync(context, alert);

        Assert.Equal(new PsaCloseResult(0, 1), result);
    }

    [Fact]
    public async Task CloseTicketsAsync_SkipsATicketThisContextHasAlreadyMarkedClosed()
    {
        await using var context = CreateContext();
        var alert = await SeedAlertAsync(context);
        var ticket = new AlertTicket { AlertEventId = alert.Id, Psa = PsaKind.HaloPsa, TicketId = "1", IsOpen = true };
        context.AlertTickets.Add(ticket);
        await context.SaveChangesAsync();
        ticket.IsOpen = false;
        var halo = new FakePsaProvider(PsaKind.HaloPsa);

        var result = await CreateService(halo).CloseTicketsAsync(context, alert);

        Assert.Equal(PsaCloseResult.None, result);
        Assert.Empty(halo.Closed);
    }
}
