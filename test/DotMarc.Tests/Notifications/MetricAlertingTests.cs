using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Psa;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>The three metric alert kinds as the monitor cycle raises them: off until a rule is switched on, then raised from the
/// domain's reports, closed when the number recovers or the rule stops applying, and scoped like any other rule.</summary>
[Collection("Postgres")]
public sealed class MetricAlertingTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private readonly FakeAlertWebhookClient _webhookClient = new();
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public MetricAlertingTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        await NotificationSettingsService.SaveAsync(context, TestActors.Admin, new NotificationSettings { Enabled = true, CooldownMinutes = 180 });
        await AlertRuleSeeder.EnsureDefaultPackAsync(context, CancellationToken.None);
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

    private async Task<int> SeedDomainAsync(string name = "contoso.io", string? groupName = null)
    {
        await using var context = CreateContext();
        var group = groupName is null ? null : await context.Groups.FirstOrDefaultAsync(candidate => candidate.Name == groupName) ?? new Group { Name = groupName };
        var domain = new Domain
        {
            Name = name, IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-90),
            LastReportReceivedUtc = DateTimeOffset.UtcNow, Groups = group is null ? [] : [group]
        };
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
        return domain.Id;
    }

    private async Task<int> GroupIdAsync(string name)
    {
        await using var context = CreateContext();
        return await context.Groups.Where(group => group.Name == name).Select(group => group.Id).SingleAsync();
    }

    private static ReportRecord Records(int messages, bool passing = true, DispositionResult disposition = DispositionResult.None) => new()
    {
        SourceIp = "203.0.113.5", HeaderFrom = "contoso.io", MessageCount = messages, Disposition = disposition,
        SpfResult = passing ? AuthResult.Pass : AuthResult.Fail, DkimResult = passing ? AuthResult.Pass : AuthResult.Fail
    };

    private async Task AddReportAsync(int domainId, double daysAgo, params ReportRecord[] records)
    {
        await using var context = CreateContext();
        var receivedUtc = DateTimeOffset.UtcNow.AddDays(-daysAgo);
        context.Reports.Add(new Report
        {
            DomainId = domainId, ReportingOrg = "google.com", ReportId = Guid.NewGuid().ToString(), RawXml = "<feedback />",
            DateRangeBeginUtc = receivedUtc.AddDays(-1), DateRangeEndUtc = receivedUtc, ReceivedUtc = receivedUtc, Records = [.. records]
        });
        await context.SaveChangesAsync();
    }

    private Task SwitchOnAsync(string kind, Action<AlertRule>? change = null) =>
        AlertRuleTestSupport.EditDefaultRuleAsync(_connectionString, kind, rule =>
        {
            rule.Enabled = true;
            change?.Invoke(rule);
        });

    private async Task<int> AddScopedRuleAsync(string kind, int groupId, bool stop)
    {
        await using var context = CreateContext();
        var rule = new AlertRule
        {
            Kind = kind, Name = "Scoped metric rule", Severity = "Warning", Position = -10, StopProcessing = stop, Enabled = true,
            ParametersJson = AlertRuleSeeder.DefaultPack().Single(candidate => candidate.Kind == kind).ParametersJson,
            Scope = [new AlertRuleScopeEntry { Target = AlertRuleScopeTarget.Group, TargetId = groupId }]
        };
        context.AlertRules.Add(rule);
        await context.SaveChangesAsync();
        return rule.Id;
    }

    private async Task RunCycleAsync()
    {
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), _webhookClient, PsaTestSupport.ForHalo(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);
        await service.CheckPinnedDomainsAsync();
    }

    private async Task<List<AlertEvent>> AlertsAsync(string alertType, string? domainName = null)
    {
        await using var context = CreateContext();
        return await context.AlertEvents
            .Where(alert => alert.AlertType == alertType && (domainName == null || alert.DomainName == domainName))
            .OrderBy(alert => alert.Id)
            .ToListAsync();
    }

    private static readonly string[] MetricTypes = [AlertTypes.PassRateLow, AlertTypes.VolumeChanged, AlertTypes.RejectedMessagesHigh];

    // ---- Off by default ----

    [Fact]
    public async Task WithTheDefaultRulesOff_NothingIsRaised_HoweverBadTheData()
    {
        var domainId = await SeedDomainAsync();
        await AddReportAsync(domainId, 10, Records(1000));
        await AddReportAsync(domainId, 1, Records(10, passing: false), Records(300, passing: false, disposition: DispositionResult.Reject));

        await RunCycleAsync();

        foreach (var alertType in MetricTypes)
        {
            Assert.Empty(await AlertsAsync(alertType));
        }
    }

    // ---- Pass rate ----

    [Fact]
    public async Task PassRate_RaisesOneAlertWithTheMessage_AndDoesNotRepeatWithinTheCooldown()
    {
        var domainId = await SeedDomainAsync();
        await AddReportAsync(domainId, 1, Records(298), Records(122, passing: false));
        await SwitchOnAsync(AlertTypes.PassRateLow);

        await RunCycleAsync();
        await RunCycleAsync();

        var alert = Assert.Single(await AlertsAsync(AlertTypes.PassRateLow));
        Assert.Equal("DMARC pass rate is low", alert.Title);
        Assert.Equal("contoso.io: DMARC pass rate was 71% over the last 7 days (threshold 90%, 420 messages).", alert.Message);
        Assert.Equal("Warning", alert.Severity);
        Assert.Equal(1, _webhookClient.Sent.Count(sent => sent.AlertType == AlertTypes.PassRateLow));
    }

    [Fact]
    public async Task PassRate_ClosesTheAlertOnTheNextCycleOnceGoodDataArrives()
    {
        var domainId = await SeedDomainAsync();
        await AddReportAsync(domainId, 1, Records(298), Records(122, passing: false));
        await SwitchOnAsync(AlertTypes.PassRateLow);
        await RunCycleAsync();
        Assert.Null(Assert.Single(await AlertsAsync(AlertTypes.PassRateLow)).ResolvedUtc);

        await AddReportAsync(domainId, 0.5, Records(2000));
        await RunCycleAsync();

        Assert.NotNull(Assert.Single(await AlertsAsync(AlertTypes.PassRateLow)).ResolvedUtc);
    }

    [Fact]
    public async Task PassRate_NeverAlertsOnADomainWithFiftyMessages()
    {
        var domainId = await SeedDomainAsync();
        await AddReportAsync(domainId, 1, Records(50, passing: false));
        await SwitchOnAsync(AlertTypes.PassRateLow);

        await RunCycleAsync();

        Assert.Empty(await AlertsAsync(AlertTypes.PassRateLow));
    }

    [Fact]
    public async Task ARaisedAlert_HasTheRulesSeverity_AndIsDeliveredOnce()
    {
        var domainId = await SeedDomainAsync();
        await AddReportAsync(domainId, 1, Records(298), Records(122, passing: false));
        await SwitchOnAsync(AlertTypes.PassRateLow, rule => rule.Severity = "Critical");

        await RunCycleAsync();

        Assert.Equal("Critical", Assert.Single(await AlertsAsync(AlertTypes.PassRateLow)).Severity);
        Assert.Equal([("contoso.io", AlertTypes.PassRateLow, "Critical")], _webhookClient.Sent.Where(sent => sent.AlertType == AlertTypes.PassRateLow));
    }

    // ---- Volume ----

    [Fact]
    public async Task Volume_RaisesForADrop_AndClosesWhenTheVolumeRecovers()
    {
        var domainId = await SeedDomainAsync();
        await AddReportAsync(domainId, 10, Records(900));
        await AddReportAsync(domainId, 2, Records(340));
        await SwitchOnAsync(AlertTypes.VolumeChanged);

        await RunCycleAsync();

        var alert = Assert.Single(await AlertsAsync(AlertTypes.VolumeChanged));
        Assert.Equal("Message volume changed", alert.Title);
        Assert.Equal("contoso.io: message volume fell 62% over the last 7 days (340 messages, 900 in the 7 days before).", alert.Message);

        await AddReportAsync(domainId, 1, Records(1000));
        await RunCycleAsync();

        Assert.NotNull(Assert.Single(await AlertsAsync(AlertTypes.VolumeChanged)).ResolvedUtc);
    }

    [Fact]
    public async Task Volume_RaisesForARise_WhenTheRuleWatchesForOne()
    {
        var domainId = await SeedDomainAsync();
        await AddReportAsync(domainId, 10, Records(100));
        await AddReportAsync(domainId, 2, Records(160));
        await SwitchOnAsync(AlertTypes.VolumeChanged, rule => AlertRuleTestSupport.SetParameter(rule, AlertRuleParameterKeys.Direction, "rise"));

        await RunCycleAsync();

        Assert.Equal("contoso.io: message volume rose 60% over the last 7 days (160 messages, 100 in the 7 days before).", Assert.Single(await AlertsAsync(AlertTypes.VolumeChanged)).Message);
    }

    // ---- Rejected or quarantined ----

    [Fact]
    public async Task Rejected_RaisesAtTheThreshold_CountingRejectAndQuarantine()
    {
        var domainId = await SeedDomainAsync();
        await AddReportAsync(domainId, 1, Records(30, disposition: DispositionResult.Reject), Records(25, disposition: DispositionResult.Quarantine));
        await SwitchOnAsync(AlertTypes.RejectedMessagesHigh);

        await RunCycleAsync();

        var alert = Assert.Single(await AlertsAsync(AlertTypes.RejectedMessagesHigh));
        Assert.Equal("Rejected or quarantined messages are high", alert.Title);
        Assert.Equal("contoso.io: 55 messages were rejected or quarantined over the last 7 days (threshold 50).", alert.Message);
    }

    [Fact]
    public async Task Rejected_DoesNotRaiseJustUnderTheThreshold()
    {
        var domainId = await SeedDomainAsync();
        await AddReportAsync(domainId, 1, Records(49, disposition: DispositionResult.Reject));
        await SwitchOnAsync(AlertTypes.RejectedMessagesHigh);

        await RunCycleAsync();

        Assert.Empty(await AlertsAsync(AlertTypes.RejectedMessagesHigh));
    }

    // ---- No reports ----

    [Fact]
    public async Task ADomainWithNoReports_RaisesNothingForAnyOfTheThree()
    {
        await SeedDomainAsync();
        foreach (var kind in MetricTypes)
        {
            await SwitchOnAsync(kind, rule =>
            {
                foreach (var definition in AlertRuleKinds.Find(kind)!.Parameters.Where(definition => definition.Key is "minimumMessages" or "messagesAtLeast"))
                {
                    AlertRuleTestSupport.SetParameter(rule, definition.Key, "1");
                }
            });
        }

        await RunCycleAsync();

        foreach (var alertType in MetricTypes)
        {
            Assert.Empty(await AlertsAsync(alertType));
        }
    }

    // ---- Rules stopping to apply ----

    [Fact]
    public async Task SwitchingTheRuleOff_ClosesItsOpenAlertOnTheNextCycle()
    {
        var domainId = await SeedDomainAsync();
        await AddReportAsync(domainId, 1, Records(55, disposition: DispositionResult.Reject));
        await SwitchOnAsync(AlertTypes.RejectedMessagesHigh);
        await RunCycleAsync();
        Assert.Null(Assert.Single(await AlertsAsync(AlertTypes.RejectedMessagesHigh)).ResolvedUtc);

        await AlertRuleTestSupport.EditDefaultRuleAsync(_connectionString, AlertTypes.RejectedMessagesHigh, rule => rule.Enabled = false);
        await RunCycleAsync();

        Assert.NotNull(Assert.Single(await AlertsAsync(AlertTypes.RejectedMessagesHigh)).ResolvedUtc);
    }

    // ---- Scope and stacking ----

    [Fact]
    public async Task ARuleScopedToAGroup_RaisesOnlyForThatGroup_WithTheSameData()
    {
        await SeedDomainAsync("in-group.test", groupName: "Aurora");
        await SeedDomainAsync("outside.test");
        await using (var context = CreateContext())
        {
            foreach (var domain in await context.Domains.ToListAsync())
            {
                context.Reports.Add(new Report
                {
                    DomainId = domain.Id, ReportingOrg = "google.com", ReportId = Guid.NewGuid().ToString(), RawXml = "<feedback />",
                    ReceivedUtc = DateTimeOffset.UtcNow.AddDays(-1), Records = [Records(298), Records(122, passing: false)]
                });
            }

            await context.SaveChangesAsync();
        }

        await AddScopedRuleAsync(AlertTypes.PassRateLow, await GroupIdAsync("Aurora"), stop: true);

        await RunCycleAsync();

        Assert.Equal("in-group.test", Assert.Single(await AlertsAsync(AlertTypes.PassRateLow)).DomainName);
    }

    [Fact]
    public async Task AScopedRuleThatDoesNotStop_RaisesItsOwnAlert_BesideTheDefaultRule()
    {
        var domainId = await SeedDomainAsync("in-group.test", groupName: "Aurora");
        await AddReportAsync(domainId, 1, Records(298), Records(122, passing: false));
        await SwitchOnAsync(AlertTypes.PassRateLow);
        var scopedRuleId = await AddScopedRuleAsync(AlertTypes.PassRateLow, await GroupIdAsync("Aurora"), stop: false);

        await RunCycleAsync();

        var alerts = await AlertsAsync(AlertTypes.PassRateLow, "in-group.test");
        Assert.Equal(2, alerts.Count);
        Assert.Contains(scopedRuleId, alerts.Select(alert => alert.AlertRuleId!.Value));
    }
}
