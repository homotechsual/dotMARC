// test/DotMarc.Tests/Notifications/AlertTicketRuleServiceTests.cs
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class AlertTicketRuleServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AlertTicketRuleServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private async Task<int> AddGroupAsync(string name)
    {
        await using var context = CreateContext();
        var group = new Group { Name = name, HaloClientId = 7 };
        context.Groups.Add(group);
        await context.SaveChangesAsync();
        return group.Id;
    }

    [Fact]
    public async Task WithNoRules_NothingIsReturned_SoEveryTypeUsesItsDefault()
    {
        await using var context = CreateContext();

        Assert.Empty(await AlertTicketRuleService.GetGlobalAsync(context));
        Assert.Empty(await AlertTicketRuleService.CountOverridesByGroupAsync(context));
    }

    [Fact]
    public async Task SetGlobalAsync_CreatesTheRule_ThenUpdatesTheSameRow()
    {
        await using (var context = CreateContext())
        {
            await AlertTicketRuleService.SetGlobalAsync(context, AlertTypes.MissedReport, false);
        }

        await using (var context = CreateContext())
        {
            await AlertTicketRuleService.SetGlobalAsync(context, AlertTypes.MissedReport, true);
        }

        await using var verify = CreateContext();
        var global = await AlertTicketRuleService.GetGlobalAsync(verify);
        Assert.Equal(new Dictionary<string, bool> { [AlertTypes.MissedReport] = true }, global);
        Assert.Equal(1, await verify.AlertTicketRules.CountAsync());
    }

    [Fact]
    public async Task SetForGroupAsync_StoresAnOverrideForThatGroupOnly()
    {
        var groupA = await AddGroupAsync("Client A");
        var groupB = await AddGroupAsync("Client B");
        await using (var context = CreateContext())
        {
            await AlertTicketRuleService.SetForGroupAsync(context, groupA, AlertTypes.TlsrptFailure, false);
        }

        await using var verify = CreateContext();
        Assert.Equal(new Dictionary<string, bool> { [AlertTypes.TlsrptFailure] = false }, await AlertTicketRuleService.GetForGroupAsync(verify, groupA));
        Assert.Empty(await AlertTicketRuleService.GetForGroupAsync(verify, groupB));
        Assert.Empty(await AlertTicketRuleService.GetGlobalAsync(verify));
    }

    [Fact]
    public async Task SetForGroupAsync_WithNull_RemovesTheOverride_SoNoNoOpRowsAccumulate()
    {
        var groupId = await AddGroupAsync("Client A");
        await using (var context = CreateContext())
        {
            await AlertTicketRuleService.SetForGroupAsync(context, groupId, AlertTypes.MissedReport, true);
        }

        await using (var context = CreateContext())
        {
            await AlertTicketRuleService.SetForGroupAsync(context, groupId, AlertTypes.MissedReport, null);
        }

        await using var verify = CreateContext();
        Assert.Equal(0, await verify.AlertTicketRules.CountAsync());
    }

    [Fact]
    public async Task CountOverridesByGroupAsync_CountsEachGroupsOverrides_AndIgnoresGlobalRows()
    {
        var groupA = await AddGroupAsync("Client A");
        var groupB = await AddGroupAsync("Client B");
        await using (var context = CreateContext())
        {
            await AlertTicketRuleService.SetGlobalAsync(context, AlertTypes.MissedReport, false);
            await AlertTicketRuleService.SetForGroupAsync(context, groupA, AlertTypes.MissedReport, true);
            await AlertTicketRuleService.SetForGroupAsync(context, groupA, AlertTypes.TlsrptFailure, false);
            await AlertTicketRuleService.SetForGroupAsync(context, groupB, AlertTypes.MissedReport, false);
        }

        await using var verify = CreateContext();
        var counts = await AlertTicketRuleService.CountOverridesByGroupAsync(verify);
        Assert.Equal(new Dictionary<int, int> { [groupA] = 2, [groupB] = 1 }, counts);
    }

    [Fact]
    public async Task DeletingAGroup_DeletesItsRules_ButNotTheGlobalOnes()
    {
        var groupId = await AddGroupAsync("Client A");
        await using (var context = CreateContext())
        {
            await AlertTicketRuleService.SetGlobalAsync(context, AlertTypes.MissedReport, false);
            await AlertTicketRuleService.SetForGroupAsync(context, groupId, AlertTypes.MissedReport, true);
        }

        await using (var context = CreateContext())
        {
            await GroupManagementService.RemoveGroupAsync(context, groupId);
        }

        await using var verify = CreateContext();
        var remaining = await verify.AlertTicketRules.ToListAsync();
        Assert.Single(remaining);
        Assert.Null(remaining[0].GroupId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Setters_RefuseAnAlertTypeThatIsNotInTheRegistry(bool forGroup)
    {
        var groupId = await AddGroupAsync("Client A");
        await using var context = CreateContext();

        var attempt = forGroup
            ? AlertTicketRuleService.SetForGroupAsync(context, groupId, "NotARealAlertType", true)
            : AlertTicketRuleService.SetGlobalAsync(context, "NotARealAlertType", true);

        await Assert.ThrowsAsync<ArgumentException>(() => attempt);
    }

    [Fact]
    public async Task TheDatabase_RefusesTwoGlobalRulesForTheSameType_AndTwoOverridesForTheSameGroupAndType()
    {
        var groupId = await AddGroupAsync("Client A");
        await using var context = CreateContext();
        context.AlertTicketRules.Add(new AlertTicketRule { AlertType = AlertTypes.MissedReport, GroupId = null, CreateTicket = true });
        context.AlertTicketRules.Add(new AlertTicketRule { AlertType = AlertTypes.MissedReport, GroupId = null, CreateTicket = false });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        await using var second = CreateContext();
        second.AlertTicketRules.Add(new AlertTicketRule { AlertType = AlertTypes.MissedReport, GroupId = groupId, CreateTicket = true });
        second.AlertTicketRules.Add(new AlertTicketRule { AlertType = AlertTypes.MissedReport, GroupId = groupId, CreateTicket = false });
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }
}
