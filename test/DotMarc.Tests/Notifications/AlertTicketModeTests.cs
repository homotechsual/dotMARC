using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Psa;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>A rule's ticket mode overrides the ticket rules: Never opens none, Always opens one even where the ticket rules say no.</summary>
[Collection("Postgres")]
public sealed class AlertTicketModeTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AlertTicketModeTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static PsaTicketService CreateService(FakePsaProvider provider) => new([provider], NullLogger<PsaTicketService>.Instance);

    /// <summary>A domain linked to a Halo company through its Group (or not linked, when <paramref name="linked"/> is false) and one new alert
    /// raised by a rule in the given ticket mode. A null mode makes an alert with no rule.</summary>
    private async Task<AlertEvent> SeedAlertAsync(string alertType, AlertTicketMode? mode, bool linked = true)
    {
        await using var context = CreateContext();
        var group = new Group { Name = "Client A", PsaCompanyLinks = linked ? [new PsaCompanyLink { Psa = PsaKind.HaloPsa, CompanyId = "7", CompanyName = "Company 7" }] : [] };
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow, Groups = [group] });
        AlertRule? rule = null;
        if (mode is { } ticketMode)
        {
            rule = new AlertRule { Kind = alertType, Name = "Rule", Severity = "Warning", TicketMode = ticketMode };
            context.AlertRules.Add(rule);
            await context.SaveChangesAsync();
        }

        var alert = new AlertEvent { DomainName = "contoso.io", AlertType = alertType, Severity = "Warning", Title = "t", Message = "m", AlertRuleId = rule?.Id };
        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync();
        return alert;
    }

    private async Task<int> TicketsAfterAsync(AlertEvent alert, FakePsaProvider provider)
    {
        await using var context = CreateContext();
        await CreateService(provider).CreateTicketsAsync(context, alert);
        await context.SaveChangesAsync();
        return provider.Created.Count;
    }

    [Fact]
    public async Task Always_OpensATicket_ForATypeThatDoesNotCreateOneByDefault()
    {
        var alert = await SeedAlertAsync(AlertTypes.NameserversChanged, AlertTicketMode.Always);

        Assert.Equal(1, await TicketsAfterAsync(alert, new FakePsaProvider(PsaKind.HaloPsa)));
    }

    [Fact]
    public async Task Always_OpensATicket_EvenWhenAGroupTicketRuleSaysNo()
    {
        var alert = await SeedAlertAsync(AlertTypes.MissedReport, AlertTicketMode.Always);
        await using (var context = CreateContext())
        {
            var groupId = (await context.Groups.SingleAsync()).Id;
            context.AlertTicketRules.Add(new AlertTicketRule { AlertType = AlertTypes.MissedReport, GroupId = groupId, CreateTicket = false });
            await context.SaveChangesAsync();
        }

        Assert.Equal(1, await TicketsAfterAsync(alert, new FakePsaProvider(PsaKind.HaloPsa)));
    }

    [Fact]
    public async Task Never_OpensNoTicket_ForATypeThatCreatesOneByDefault()
    {
        var alert = await SeedAlertAsync(AlertTypes.MissedReport, AlertTicketMode.Never);

        Assert.Equal(0, await TicketsAfterAsync(alert, new FakePsaProvider(PsaKind.HaloPsa)));
    }

    [Fact]
    public async Task FollowRules_UsesTheTicketRules()
    {
        var follows = await SeedAlertAsync(AlertTypes.MissedReport, AlertTicketMode.FollowRules);

        Assert.Equal(1, await TicketsAfterAsync(follows, new FakePsaProvider(PsaKind.HaloPsa)));
    }

    [Fact]
    public async Task AnAlertWithNoRule_UsesTheTicketRules()
    {
        var alert = await SeedAlertAsync(AlertTypes.NameserversChanged, mode: null);

        Assert.Equal(0, await TicketsAfterAsync(alert, new FakePsaProvider(PsaKind.HaloPsa)));
    }

    [Fact]
    public async Task Always_WithNoCompanyLinked_OpensNothingAndDoesNotThrow()
    {
        var alert = await SeedAlertAsync(AlertTypes.NameserversChanged, AlertTicketMode.Always, linked: false);

        Assert.Equal(0, await TicketsAfterAsync(alert, new FakePsaProvider(PsaKind.HaloPsa)));
    }

    [Fact]
    public async Task Always_ForAnAlertWithNoDomain_OpensNothingAndDoesNotThrow()
    {
        await using var context = CreateContext();
        var rule = new AlertRule { Kind = AlertTypes.ApiKeyExpiring, Name = "Rule", Severity = "Warning", TicketMode = AlertTicketMode.Always };
        context.AlertRules.Add(rule);
        await context.SaveChangesAsync();
        var alert = new AlertEvent { DomainName = "API key: reports", AlertType = AlertTypes.ApiKeyExpiring, Severity = "Warning", Title = "t", Message = "m", AlertRuleId = rule.Id };
        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync();

        Assert.Equal(0, await TicketsAfterAsync(alert, new FakePsaProvider(PsaKind.HaloPsa)));
    }
}
