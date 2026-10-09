using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Email;
using DotMarc.Portal;
using DotMarc.Reporting.ClientReports;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

[Collection("Postgres")]
public sealed class ClientReportRunnerTests : IAsyncLifetime
{
    private sealed class RecordingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
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

    public ClientReportRunnerTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private ClientReportRunner Runner(IEmailSender? sender, RecordingAlerts? alerts = null)
    {
        var factory = new FakeDbContextFactory(_connectionString);
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero));
        return new ClientReportRunner(factory, new ClientReportBuilder(factory, new PortalBrandLoader(factory), clock), new FixedSenderFactory(sender), alerts ?? new RecordingAlerts(), clock);
    }

    private async Task<int> AddGroupAsync()
    {
        await using var context = CreateContext();
        var group = new Group { Name = "Aurora Retail" };
        context.Domains.Add(new Domain { Name = "aurora-retail.example", IsMonitored = true, Groups = [group] });
        await context.SaveChangesAsync();
        return group.Id;
    }

    private static readonly ReportPeriod March = new(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), ReportPeriodKind.Month);

    [Fact]
    public async Task SendNow_SendsRecordsAndAudits()
    {
        var groupId = await AddGroupAsync();
        var sender = new RecordingSender();
        var alerts = new RecordingAlerts();

        var result = await Runner(sender, alerts).SendNowAsync(TestActors.Admin, groupId, March, ["it@aurora-retail.example"], CancellationToken.None);

        Assert.True(result.Sent);
        Assert.Single(sender.Sent);
        Assert.Contains(groupId, alerts.Resolved);
        await using var verify = CreateContext();
        var delivery = await verify.ClientReportDeliveries.SingleAsync();
        Assert.Equal((ClientReportDeliveryKind.Manual, ClientReportDeliveryStatus.Sent, "admin@example.com"), (delivery.Kind, delivery.Status, delivery.RequestedBy));
        Assert.Equal(AuditActions.ClientReportSent, (await verify.AuditEntries.SingleAsync()).Action);
    }

    [Fact]
    public async Task SendNow_WithEmailOff_SaysSo_AndRecordsNothing()
    {
        var groupId = await AddGroupAsync();

        var result = await Runner(sender: null).SendNowAsync(TestActors.Admin, groupId, March, ["it@aurora-retail.example"], CancellationToken.None);

        Assert.Equal((false, "Email is off. Set it up on Email & reports first."), (result.Sent, result.Message));
        await using var verify = CreateContext();
        Assert.Empty(verify.ClientReportDeliveries);
    }

    [Fact]
    public async Task SendNow_RefusesBadRecipients_BeforeBuildingAnything()
    {
        var groupId = await AddGroupAsync();

        var result = await Runner(new RecordingSender()).SendNowAsync(TestActors.Admin, groupId, March, ["nope"], CancellationToken.None);

        Assert.Equal((false, "nope isn't a valid email address."), (result.Sent, result.Message));
    }

    [Fact]
    public async Task SendNow_WhenTheDatabaseFails_SaysSo_RatherThanThrowing()
    {
        var unreachable = new FakeDbContextFactory("Host=127.0.0.1;Port=1;Database=nowhere;Username=nobody;Password=none;Timeout=2");
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero));
        var runner = new ClientReportRunner(unreachable, new ClientReportBuilder(unreachable, new PortalBrandLoader(unreachable), clock),
            new FixedSenderFactory(new RecordingSender()), new RecordingAlerts(), clock);

        var result = await runner.SendNowAsync(TestActors.Admin, 1, March, ["it@aurora-retail.example"], CancellationToken.None);

        Assert.False(result.Sent);
        Assert.StartsWith("Couldn't send the report:", result.Message);
    }

    [Fact]
    public async Task Render_ReturnsAPdfNamedForThePeriod()
    {
        var groupId = await AddGroupAsync();

        var rendered = await Runner(new RecordingSender()).RenderAsync(groupId, March, CancellationToken.None);

        Assert.Equal("Aurora Retail email security report March 2026.pdf", rendered!.Value.FileName);
        Assert.Equal("%PDF-"u8.ToArray(), rendered.Value.Pdf[..5]);
    }
}
