using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Psa;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>An alert that stays open past the cooldown is raised again as a new row, and the new row is not given a second
/// ticket while an earlier copy's is open. The ticket still belongs to the condition, so every open copy has to show it.</summary>
[Collection("Postgres")]
public sealed class AlertTicketLinksTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AlertTicketLinksTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private async Task<AlertEvent> AddAlertAsync(string domain = "contoso.io", string type = AlertTypes.SuspiciousRejectActivity, bool resolved = false)
    {
        await using var context = CreateContext();
        var alert = new AlertEvent { DomainName = domain, AlertType = type, Severity = "Warning", Title = "t", Message = "m", IsResolved = resolved };
        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync();
        return alert;
    }

    private async Task AddTicketAsync(int alertId, string ticketId, PsaKind psa = PsaKind.HaloPsa, bool open = true)
    {
        await using var context = CreateContext();
        context.AlertTickets.Add(new AlertTicket { AlertEventId = alertId, Psa = psa, TicketId = ticketId, IsOpen = open });
        await context.SaveChangesAsync();
    }

    private async Task<ILookup<int, AlertTicket>> LoadAsync(params AlertEvent[] alerts)
    {
        await using var context = CreateContext();
        return await AlertTicketLinks.LoadAsync(context, alerts);
    }

    [Fact]
    public async Task AnOpenAlert_ShowsTheOpenTicketOfAnEarlierOpenCopy_WhichIsWhyItWasNotGivenOneOfItsOwn()
    {
        var earlier = await AddAlertAsync();
        await AddTicketAsync(earlier.Id, "900");
        var reRaised = await AddAlertAsync();

        var tickets = await LoadAsync(reRaised, earlier);

        Assert.Equal(["900"], tickets[reRaised.Id].Select(ticket => ticket.TicketId));
        Assert.Equal(["900"], tickets[earlier.Id].Select(ticket => ticket.TicketId));
    }

    [Fact]
    public async Task AnAlertsOwnTickets_AreAlwaysKept_EvenClosedOnes_WithoutDuplicates()
    {
        var alert = await AddAlertAsync(resolved: true);
        await AddTicketAsync(alert.Id, "901", open: false);

        var tickets = await LoadAsync(alert);

        Assert.Equal(["901"], tickets[alert.Id].Select(ticket => ticket.TicketId));
    }

    [Fact]
    public async Task AResolvedAlert_DoesNotTakeTheTicketOfAnOpenCopy()
    {
        var open = await AddAlertAsync();
        await AddTicketAsync(open.Id, "902");
        var resolved = await AddAlertAsync(resolved: true);

        var tickets = await LoadAsync(resolved);

        Assert.Empty(tickets[resolved.Id]);
    }

    [Fact]
    public async Task AClosedTicketOnAnEarlierCopy_IsNotShownOnTheNewOne()
    {
        var earlier = await AddAlertAsync();
        await AddTicketAsync(earlier.Id, "903", open: false);
        var reRaised = await AddAlertAsync();

        var tickets = await LoadAsync(reRaised);

        Assert.Empty(tickets[reRaised.Id]);
    }

    [Fact]
    public async Task ADifferentDomainOrAlertType_DoesNotShareATicket()
    {
        var elsewhere = await AddAlertAsync("other.io");
        await AddTicketAsync(elsewhere.Id, "904");
        var otherType = await AddAlertAsync(type: AlertTypes.MissedReport);
        await AddTicketAsync(otherType.Id, "905");
        var subject = await AddAlertAsync();

        var tickets = await LoadAsync(subject);

        Assert.Empty(tickets[subject.Id]);
    }

    [Fact]
    public async Task SeveralOpenCopies_EachShowTheOneTicket_OnlyOnce()
    {
        var first = await AddAlertAsync();
        await AddTicketAsync(first.Id, "906");
        var second = await AddAlertAsync();
        var third = await AddAlertAsync();

        var tickets = await LoadAsync(first, second, third);

        Assert.All(new[] { first, second, third }, alert => Assert.Equal(["906"], tickets[alert.Id].Select(ticket => ticket.TicketId)));
    }

    [Fact]
    public async Task NoAlerts_IsAnEmptyLookup()
    {
        Assert.Empty(await LoadAsync());
    }
}
