using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Email;
using DotMarc.Portal;
using DotMarc.Reporting.ClientReports;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

[Collection("Postgres")]
public sealed class ClientReportDispatcherTests : IAsyncLifetime
{
    private sealed class RecordingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public Exception? FailWith { get; set; }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            if (FailWith is not null)
            {
                throw FailWith;
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedSenderFactory(IEmailSender? sender) : IEmailSenderFactory
    {
        public Task<IEmailSender?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(sender);
    }

    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;
    private readonly RecordingSender _sender = new();
    private readonly RecordingAlerts _alerts = new();

    // 1 April 2026 05:00 UTC: March is complete but not yet due (due 06:00 UTC).
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 4, 1, 5, 0, 0, TimeSpan.Zero));

    public ClientReportDispatcherTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private ClientReportDispatcher Dispatcher(IEmailSender? sender = null, bool emailOff = false)
    {
        var factory = new FakeDbContextFactory(_connectionString);
        return new ClientReportDispatcher(factory, new ClientReportBuilder(factory, new PortalBrandLoader(factory), _clock),
            new FixedSenderFactory(emailOff ? null : sender ?? _sender), _alerts, _clock, NullLogger<ClientReportDispatcher>.Instance);
    }

    /// <summary>A Group with one monitored domain and a monthly schedule started in February.</summary>
    private async Task<int> AddScheduledGroupAsync(string name = "Aurora Retail", ReportFrequency frequency = ReportFrequency.Monthly, bool withDomain = true)
    {
        await using var context = CreateContext();
        var group = new Group { Name = name };
        context.Groups.Add(group);
        if (withDomain)
        {
            context.Domains.Add(new Domain { Name = $"{name.ToLowerInvariant().Replace(' ', '-')}.example", IsMonitored = true, Groups = [group] });
        }

        await context.SaveChangesAsync();
        context.GroupReportSchedules.Add(new GroupReportSchedule
        {
            GroupId = group.Id, Frequency = frequency, Recipients = ["it@aurora-retail.example"], StartedUtc = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await context.SaveChangesAsync();
        return group.Id;
    }

    private async Task<ClientReportDelivery> DeliveryAsync(int groupId)
    {
        await using var context = CreateContext();
        return await context.ClientReportDeliveries.SingleAsync(delivery => delivery.GroupId == groupId);
    }

    [Fact]
    public async Task NothingIsSent_BeforeTheSendHour()
    {
        await AddScheduledGroupAsync();

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task ADuePeriod_IsSentOnce()
    {
        var groupId = await AddScheduledGroupAsync();
        _clock.Advance(TimeSpan.FromHours(1));

        await Dispatcher().RunOnceAsync(CancellationToken.None);
        await Dispatcher().RunOnceAsync(CancellationToken.None);

        var sent = Assert.Single(_sender.Sent);
        Assert.Equal("Aurora Retail email security report: March 2026", sent.Subject);
        var delivery = await DeliveryAsync(groupId);
        Assert.Equal((ClientReportDeliveryStatus.Sent, new DateOnly(2026, 3, 1), ClientReportDeliveryKind.Scheduled), (delivery.Status, delivery.PeriodStart, delivery.Kind));
    }

    [Fact]
    public async Task ASchedule_TurnedOnMidPeriod_WaitsForTheNextPeriod()
    {
        var groupId = await AddScheduledGroupAsync(frequency: ReportFrequency.Weekly);
        _clock.Advance(TimeSpan.FromDays(14)); // 15 April 05:00 UTC
        await using (var context = CreateContext())
        {
            // Switching to monthly on 15 April restarts the schedule then, through the real path.
            await ClientReportService.SetScheduleAsync(context, TestActors.Admin, groupId, ReportFrequency.Monthly, ["it@aurora-retail.example"], _clock);
        }

        _clock.Advance(TimeSpan.FromDays(1)); // 16 April: March is due but fell due before the schedule started

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Empty(_sender.Sent);
        await using var verify = CreateContext();
        Assert.False(await verify.ClientReportDeliveries.AnyAsync(delivery => delivery.GroupId == groupId));
    }

    [Fact]
    public async Task AFailure_IsRetriedHourly_ThenGivenUpAfter24Hours_WithAnAlert()
    {
        var groupId = await AddScheduledGroupAsync();
        var failing = new RecordingSender { FailWith = new EmailSendException("535 Authentication failed") };
        _clock.Advance(TimeSpan.FromHours(1));

        await Dispatcher(failing).RunOnceAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(30));
        await Dispatcher(failing).RunOnceAsync(CancellationToken.None); // too soon to retry
        Assert.Equal((1, ClientReportDeliveryStatus.Pending), ((await DeliveryAsync(groupId)).Attempts, (await DeliveryAsync(groupId)).Status));

        for (var hour = 0; hour < 24; hour++)
        {
            _clock.Advance(TimeSpan.FromHours(1));
            await Dispatcher(failing).RunOnceAsync(CancellationToken.None);
        }

        var delivery = await DeliveryAsync(groupId);
        Assert.Equal((ClientReportDeliveryStatus.Failed, "535 Authentication failed"), (delivery.Status, delivery.Error));
        Assert.Equal((groupId, "March 2026"), (_alerts.Raised.Single().GroupId, _alerts.Raised.Single().PeriodLabel));
    }

    [Fact]
    public async Task ALaterSuccess_ResolvesTheAlert()
    {
        var groupId = await AddScheduledGroupAsync();
        _clock.Advance(TimeSpan.FromHours(1));

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Contains(groupId, _alerts.Resolved);
    }

    [Fact]
    public async Task ASuccessAfterARename_StillResolvesTheAlert()
    {
        var groupId = await AddScheduledGroupAsync();
        await using (var context = CreateContext())
        {
            (await context.Groups.SingleAsync(group => group.Id == groupId)).Name = "Aurora Retail Group";
            await context.SaveChangesAsync();
        }

        _clock.Advance(TimeSpan.FromHours(1));
        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Contains(groupId, _alerts.Resolved); // resolved by Group id, not name
    }

    [Fact]
    public async Task OneFailingGroup_DoesntStopTheOthers()
    {
        await AddScheduledGroupAsync("Aurora Retail");
        await AddScheduledGroupAsync("Brightline Legal");
        var sometimesFailing = new FailFirstSender();
        _clock.Advance(TimeSpan.FromHours(1));

        await Dispatcher(sometimesFailing).RunOnceAsync(CancellationToken.None);

        Assert.Single(sometimesFailing.Sent);
    }

    [Fact]
    public async Task EmailOff_SkipsThePeriod_WithoutAnAlert()
    {
        var groupId = await AddScheduledGroupAsync();
        _clock.Advance(TimeSpan.FromHours(1));

        await Dispatcher(emailOff: true).RunOnceAsync(CancellationToken.None);

        var delivery = await DeliveryAsync(groupId);
        Assert.Equal((ClientReportDeliveryStatus.Skipped, "Email is off"), (delivery.Status, delivery.Error));
        Assert.Empty(_alerts.Raised);
    }

    [Fact]
    public async Task AGroupWithNoDomains_IsSkipped()
    {
        var groupId = await AddScheduledGroupAsync(withDomain: false);
        _clock.Advance(TimeSpan.FromHours(1));

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Equal((ClientReportDeliveryStatus.Skipped, "No domains"), ((await DeliveryAsync(groupId)).Status, (await DeliveryAsync(groupId)).Error));
    }

    [Fact]
    public async Task AManualSend_DoesntBlockTheScheduledOne()
    {
        var groupId = await AddScheduledGroupAsync();
        await using (var context = CreateContext())
        {
            context.ClientReportDeliveries.Add(new ClientReportDelivery
            {
                GroupId = groupId, PeriodStart = new DateOnly(2026, 3, 1), PeriodEnd = new DateOnly(2026, 3, 31), Kind = ClientReportDeliveryKind.Manual,
                Recipients = ["it@aurora-retail.example"], Status = ClientReportDeliveryStatus.Sent, SentUtc = new DateTimeOffset(2026, 3, 31, 12, 0, 0, TimeSpan.Zero),
            });
            await context.SaveChangesAsync();
        }

        _clock.Advance(TimeSpan.FromHours(1));
        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task ChangingTheTimeZone_DoesntResendAPeriod()
    {
        await AddScheduledGroupAsync();
        _clock.Advance(TimeSpan.FromHours(1));
        await Dispatcher().RunOnceAsync(CancellationToken.None);
        await using (var context = CreateContext())
        {
            var settings = await context.ReportSettings.SingleAsync();
            settings.TimeZoneId = "Australia/Sydney";
            await context.SaveChangesAsync();
        }

        _clock.Advance(TimeSpan.FromHours(2));
        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task ARetry_GoesToTheCurrentRecipients()
    {
        await AddScheduledGroupAsync();
        var failing = new RecordingSender { FailWith = new EmailSendException("Mailbox unavailable") };
        _clock.Advance(TimeSpan.FromHours(1));
        await Dispatcher(failing).RunOnceAsync(CancellationToken.None);
        await using (var context = CreateContext())
        {
            (await context.GroupReportSchedules.SingleAsync()).Recipients = ["finance@aurora-retail.example"];
            await context.SaveChangesAsync();
        }

        _clock.Advance(TimeSpan.FromHours(1));
        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Equal(["finance@aurora-retail.example"], _sender.Sent.Single().To);
    }

    [Fact]
    public async Task TwoInstancesRunningAtOnce_SendTheReportOnce()
    {
        await AddScheduledGroupAsync();
        _clock.Advance(TimeSpan.FromHours(1));
        var blocking = new BlockingSender();

        var first = Dispatcher(blocking).RunOnceAsync(CancellationToken.None);
        await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var second = Dispatcher(blocking).RunOnceAsync(CancellationToken.None);
        await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(10)));
        blocking.Release.SetResult(); // always released, so a second send shows up as a second call rather than a hang
        await Task.WhenAll(first, second);

        Assert.Equal(1, blocking.Calls);
    }

    [Fact]
    public async Task ASentReport_StaysSent_WhenResolvingTheAlertFails()
    {
        var groupId = await AddScheduledGroupAsync();
        _clock.Advance(TimeSpan.FromHours(1));
        var factory = new FakeDbContextFactory(_connectionString);
        var dispatcher = new ClientReportDispatcher(factory, new ClientReportBuilder(factory, new PortalBrandLoader(factory), _clock),
            new FixedSenderFactory(_sender), new ThrowingAlerts(), _clock, NullLogger<ClientReportDispatcher>.Instance);

        await dispatcher.RunOnceAsync(CancellationToken.None);

        var delivery = await DeliveryAsync(groupId);
        Assert.Equal((ClientReportDeliveryStatus.Sent, (string?)null), (delivery.Status, delivery.Error));
    }

    private sealed class BlockingSender : IEmailSender
    {
        private int _calls;
        public int Calls => _calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            await Release.Task;
        }
    }

    private sealed class ThrowingAlerts : DotMarc.Notifications.IAlertingService
    {
        public Task CheckPinnedDomainsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResolveDomainAlertAsync(string domainName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task HandleTlsrptReportAsync(string domainName, long failedSessionCount, IReadOnlyList<string> failureTypes, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, DotMarc.Reporting.ReasonBreakdown reasonBreakdown, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RaiseClientReportFailedAsync(int groupId, string groupName, string periodLabel, string error, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResolveClientReportFailedAsync(int groupId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The alert store is down.");
    }

    private sealed class FailFirstSender : IEmailSender
    {
        private int _calls;
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            if (_calls++ == 0)
            {
                throw new EmailSendException("First one fails");
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
