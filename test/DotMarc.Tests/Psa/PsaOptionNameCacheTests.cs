using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Psa;
using DotMarc.Psa.Autotask;
using DotMarc.Psa.ConnectWise;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Psa;

/// <summary>Loading a PSA's options stores the names of the saved choices at once, so the settings page shows names
/// after a reload even if nobody saved after loading (including choices saved before names were stored at all).</summary>
[Collection("Postgres")]
public sealed class PsaOptionNameCacheTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public PsaOptionNameCacheTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    [Fact]
    public async Task HaloPsa_SavedChoicesGetTheirNames_AndAChoiceNotInTheListKeepsItsName()
    {
        await using (var context = CreateContext())
        {
            var settings = await context.HaloPsaSettings.SingleAsync();
            settings.TicketTypeId = 5;
            settings.DefaultPriorityId = 2;
            settings.ClosedStatusId = 9;
            settings.ClosedStatusName = "Closed";
            settings.AssignedAgentId = 14;
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            await PsaOptionNameCache.RememberHaloAsync(context,
                [new HaloTicketType(5, "Incident"), new HaloTicketType(6, "Request")],
                [new HaloPriority(2, "P2 High")],
                [new HaloTicketStatus(1, "New")],
                [new HaloAgent(14, "Mikey")]);
        }

        await using var verify = CreateContext();
        var saved = await verify.HaloPsaSettings.SingleAsync();
        Assert.Equal(("Incident", "P2 High", "Closed", "Mikey"), (saved.TicketTypeName, saved.DefaultPriorityName, saved.ClosedStatusName, saved.AssignedAgentName));
        Assert.Empty(verify.AuditEntries);
    }

    [Fact]
    public async Task ConnectWise_SavedChoicesGetTheirNames()
    {
        await using (var context = CreateContext())
        {
            var settings = await context.ConnectWiseSettings.SingleAsync();
            settings.BoardId = 1;
            settings.StatusId = 10;
            settings.TypeId = 20;
            settings.PriorityId = 3;
            settings.ClosedStatusId = 11;
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            await PsaOptionNameCache.RememberConnectWiseAsync(context,
                boards: [new PsaOption(1, "Service Desk")],
                statuses: [new PsaOption(10, "New"), new PsaOption(11, "Closed (resolved)")],
                types: [new PsaOption(20, "Email security")],
                priorities: [new PsaOption(3, "Priority 3 - Normal")]);
        }

        await using var verify = CreateContext();
        var saved = await verify.ConnectWiseSettings.SingleAsync();
        Assert.Equal(("Service Desk", "New", "Email security", "Priority 3 - Normal", "Closed (resolved)"),
            (saved.BoardName, saved.StatusName, saved.TypeName, saved.PriorityName, saved.ClosedStatusName));
    }

    [Fact]
    public async Task Autotask_SavedChoicesGetTheirNames()
    {
        await using (var context = CreateContext())
        {
            var settings = await context.AutotaskSettings.SingleAsync();
            settings.QueueId = 100;
            settings.TicketTypeId = 1;
            settings.IssueTypeId = 7;
            settings.PriorityId = 2;
            settings.ClosedStatusId = 5;
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            await PsaOptionNameCache.RememberAutotaskAsync(context, new AutotaskTicketPicklists(
                Queues: [new PsaOption(100, "Client Portal")],
                TicketTypes: [new PsaOption(1, "Service Request")],
                IssueTypes: [new PsaOption(7, "Email")],
                Priorities: [new PsaOption(2, "Medium")],
                Statuses: [new PsaOption(5, "Complete")]));
        }

        await using var verify = CreateContext();
        var saved = await verify.AutotaskSettings.SingleAsync();
        Assert.Equal(("Client Portal", "Service Request", "Email", "Medium", "Complete"),
            (saved.QueueName, saved.TicketTypeName, saved.IssueTypeName, saved.PriorityName, saved.ClosedStatusName));
    }
}
