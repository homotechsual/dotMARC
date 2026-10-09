using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Reporting.ClientReports;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

[Collection("Postgres")]
public sealed class ClientReportServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ClientReportServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private async Task<Group> AddGroupAsync(DotMarcDbContext context, string name = "Aurora Retail")
    {
        var group = new Group { Name = name };
        context.Groups.Add(group);
        await context.SaveChangesAsync();
        return group;
    }

    [Fact]
    public async Task SetSchedule_StoresTidiedRecipients_AndAudits()
    {
        await using var context = CreateContext();
        var group = await AddGroupAsync(context);

        await ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Monthly, [" IT@Aurora-Retail.example ", "it@aurora-retail.example", "finance@aurora-retail.example"]);

        await using var verify = CreateContext();
        var schedule = await ClientReportService.GetScheduleAsync(verify, group.Id);
        Assert.Equal(ReportFrequency.Monthly, schedule!.Frequency);
        Assert.Equal(["finance@aurora-retail.example", "it@aurora-retail.example"], schedule.Recipients);
        Assert.Equal(AuditActions.GroupReportScheduleChanged, (await verify.AuditEntries.SingleAsync()).Action);
    }

    [Theory]
    [InlineData(new[] { "not-an-email" }, "not-an-email isn't a valid email address.")]
    [InlineData(new string[0], "Add at least one recipient, or turn the schedule off.")]
    public async Task SetSchedule_RefusesBadRecipients(string[] recipients, string message)
    {
        await using var context = CreateContext();
        var group = await AddGroupAsync(context);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Weekly, recipients));

        Assert.StartsWith(message, exception.Message);
    }

    [Fact]
    public async Task SetSchedule_RefusesMoreThan25Recipients()
    {
        await using var context = CreateContext();
        var group = await AddGroupAsync(context);
        var recipients = Enumerable.Range(1, 26).Select(number => $"person{number}@aurora-retail.example").ToList();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Weekly, recipients));

        Assert.StartsWith("A report can go to at most 25 recipients.", exception.Message);
    }

    [Fact]
    public async Task ChangingTheFrequency_RestartsTheSchedule_ButEditingRecipientsDoesnt()
    {
        await using var context = CreateContext();
        var group = await AddGroupAsync(context);
        await ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Monthly, ["it@aurora-retail.example"]);
        var started = (await ClientReportService.GetScheduleAsync(context, group.Id))!.StartedUtc;

        await ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Monthly, ["it@aurora-retail.example", "ceo@aurora-retail.example"]);
        var afterRecipients = (await ClientReportService.GetScheduleAsync(context, group.Id))!.StartedUtc;
        await Task.Delay(20);
        await ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Quarterly, ["it@aurora-retail.example"]);
        var afterFrequency = (await ClientReportService.GetScheduleAsync(context, group.Id))!.StartedUtc;

        Assert.Equal(started, afterRecipients);
        Assert.True(afterFrequency > started);
    }

    [Fact]
    public async Task ChangingTheFrequency_SkipsAScheduledReportStillRetrying()
    {
        await using var context = CreateContext();
        var group = await AddGroupAsync(context);
        await ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Monthly, ["it@aurora-retail.example"]);
        context.ClientReportDeliveries.Add(new ClientReportDelivery
        {
            GroupId = group.Id, PeriodStart = new DateOnly(2026, 3, 1), PeriodEnd = new DateOnly(2026, 3, 31), Kind = ClientReportDeliveryKind.Scheduled,
            Recipients = ["it@aurora-retail.example"], Status = ClientReportDeliveryStatus.Pending, Attempts = 3, Error = "Mailbox unavailable",
        });
        await context.SaveChangesAsync();

        await ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Weekly, ["it@aurora-retail.example"]);

        await using var verify = CreateContext();
        var delivery = await verify.ClientReportDeliveries.SingleAsync();
        Assert.Equal((ClientReportDeliveryStatus.Skipped, "The schedule changed"), (delivery.Status, delivery.Error));
    }

    [Fact]
    public async Task TheScheduleStarts_ByTheGivenClock()
    {
        await using var context = CreateContext();
        var group = await AddGroupAsync(context);
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 3, 15, 9, 30, 0, TimeSpan.Zero));

        await ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Monthly, ["it@aurora-retail.example"], clock);

        Assert.Equal(clock.GetUtcNow(), (await ClientReportService.GetScheduleAsync(context, group.Id))!.StartedUtc);
    }

    [Fact]
    public async Task TheNextReport_IsTheFollowingPeriod_OnceTheLastIsSent_HoweverManyManualSendsCameSince()
    {
        await using var context = CreateContext();
        var group = await AddGroupAsync(context);
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero));
        await ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Monthly, ["it@aurora-retail.example"], clock);
        context.ClientReportDeliveries.Add(new ClientReportDelivery
        {
            GroupId = group.Id, PeriodStart = new DateOnly(2026, 3, 1), PeriodEnd = new DateOnly(2026, 3, 31), Kind = ClientReportDeliveryKind.Scheduled,
            Status = ClientReportDeliveryStatus.Sent, SentUtc = new DateTimeOffset(2026, 4, 1, 6, 0, 0, TimeSpan.Zero),
        });
        for (var day = 2; day <= 13; day++)
        {
            context.ClientReportDeliveries.Add(new ClientReportDelivery
            {
                GroupId = group.Id, PeriodStart = new DateOnly(2026, 3, 1), PeriodEnd = new DateOnly(2026, 3, 31), Kind = ClientReportDeliveryKind.Manual,
                Status = ClientReportDeliveryStatus.Sent, SentUtc = new DateTimeOffset(2026, 4, day, 9, 0, 0, TimeSpan.Zero),
            });
        }

        await context.SaveChangesAsync();
        var schedule = (await ClientReportService.GetScheduleAsync(context, group.Id))!;

        var next = await ClientReportService.GetNextScheduledAsync(context, schedule, TimeZoneInfo.Utc, sendHour: 6, new DateTimeOffset(2026, 4, 20, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(("April 2026", new DateTimeOffset(2026, 5, 1, 6, 0, 0, TimeSpan.Zero)), (next.Period.Label, next.DueUtc));
    }

    [Fact]
    public async Task TurningItOffWithNoRecipients_RemovesTheSchedule()
    {
        await using var context = CreateContext();
        var group = await AddGroupAsync(context);
        await ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Monthly, ["it@aurora-retail.example"]);

        await ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Off, []);

        await using var verify = CreateContext();
        Assert.Null(await ClientReportService.GetScheduleAsync(verify, group.Id));
    }

    [Fact]
    public async Task Suggestions_AreTheEmailsOfGrantsScopedToTheGroup()
    {
        await using var context = CreateContext();
        var aurora = await AddGroupAsync(context);
        var brightline = await AddGroupAsync(context, "Brightline Legal");
        var viewer = new Role { Name = "Client viewer", IsScopable = true, Permissions = [Permission.DomainsView] };
        context.UserAccesses.AddRange(
            new UserAccess { Email = "it@aurora-retail.example", Role = viewer, ScopedGroups = [aurora] },
            new UserAccess { Email = "partner@brightline-legal.example", Role = viewer, ScopedGroups = [brightline] });
        await context.SaveChangesAsync();

        Assert.Equal(["it@aurora-retail.example"], await ClientReportService.ListSuggestedRecipientsAsync(context, aurora.Id));
    }

    [Fact]
    public async Task DeletingAGroup_DeletesItsScheduleAndDeliveries()
    {
        await using var context = CreateContext();
        var group = await AddGroupAsync(context);
        await ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Monthly, ["it@aurora-retail.example"]);
        context.ClientReportDeliveries.Add(new ClientReportDelivery
        {
            GroupId = group.Id, PeriodStart = new DateOnly(2026, 3, 1), PeriodEnd = new DateOnly(2026, 3, 31),
            Kind = ClientReportDeliveryKind.Scheduled, Recipients = ["it@aurora-retail.example"], Status = ClientReportDeliveryStatus.Sent,
        });
        await context.SaveChangesAsync();

        await GroupManagementService.RemoveGroupAsync(context, TestActors.Admin, group.Id);

        await using var verify = CreateContext();
        Assert.Equal((0, 0), (await verify.GroupReportSchedules.CountAsync(), await verify.ClientReportDeliveries.CountAsync()));
    }
}
