using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.IpEnrichment;
using DotMarc.Notifications;
using DotMarc.Reporting;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>The two sender alerts as the alerting cycle raises and resolves them.</summary>
[Collection("Postgres")]
public sealed class SenderAlertingTests : IAsyncLifetime
{
    private const string DomainName = "example.com";
    private const string MailchimpIp = "198.51.100.7";
    private const string SendGridIp = "198.51.100.20";

    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public SenderAlertingTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        await SaveSettingsAsync();
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

    /// <summary>Sets the sender alert thresholds and switches. Alerting reads the default rules, so they are set there.</summary>
    private async Task SaveSettingsAsync(
        bool newSenderEnabled = true, int newSenderMinVolume = 20, int newSenderDays = 7, bool deliveredEnabled = true, int deliveredMinVolume = 10)
    {
        await using (var context = CreateContext())
        {
            await NotificationSettingsService.SaveAsync(context, TestActors.Admin, new NotificationSettings
            {
                Enabled = true, TeamsEnabled = true, TeamsWebhookUrl = "https://example.test/webhook",
            });
        }

        await AlertRuleTestSupport.EditDefaultRuleAsync(_connectionString, AlertTypes.NewUnreviewedSender, rule =>
        {
            rule.Enabled = newSenderEnabled;
            AlertRuleTestSupport.SetParameter(rule, AlertRuleParameterKeys.MinimumMessages, newSenderMinVolume.ToString());
            AlertRuleTestSupport.SetParameter(rule, AlertRuleParameterKeys.NewDays, newSenderDays.ToString());
        });
        await AlertRuleTestSupport.EditDefaultRuleAsync(_connectionString, AlertTypes.UnauthorisedSenderDelivered, rule =>
        {
            rule.Enabled = deliveredEnabled;
            AlertRuleTestSupport.SetParameter(rule, AlertRuleParameterKeys.MinimumMessages, deliveredMinVolume.ToString());
        });
    }

    private async Task<int> SeedDomainAsync(string name = DomainName, DmarcPolicyLevel policy = DmarcPolicyLevel.Reject, SpfCheckStatus spf = SpfCheckStatus.Ok, string? groupName = null)
    {
        await using var context = CreateContext();
        var domain = new Domain
        {
            Name = name, IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-90), LastReportReceivedUtc = DateTimeOffset.UtcNow,
            DmarcPolicy = policy, SpfCheckStatus = spf,
            Groups = groupName is null ? [] : [new Group { Name = groupName }],
        };
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
        return domain.Id;
    }

    private static ReportRecord Mail(string ip, int count, string authDomain, DispositionResult disposition = DispositionResult.None, bool passing = true)
    {
        var record = new ReportRecord
        {
            SourceIp = ip, MessageCount = count, Disposition = disposition, HeaderFrom = DomainName,
            SpfResult = passing ? AuthResult.Pass : AuthResult.Fail, DkimResult = AuthResult.Fail,
        };
        record.AuthDetails.Add(new ReportRecordAuthDetail { Mechanism = DmarcAuthMechanism.Dkim, Domain = authDomain, Result = DmarcMechanismResult.Pass });
        return record;
    }

    private async Task AddReportAsync(int domainId, double daysAgo, params ReportRecord[] records)
    {
        await using var context = CreateContext();
        var received = DateTimeOffset.UtcNow.AddDays(-daysAgo);
        context.Reports.Add(new Report
        {
            DomainId = domainId, ReportingOrg = "receiver.example", ReportId = Guid.NewGuid().ToString(), RawXml = "<feedback/>",
            DateRangeBeginUtc = received.AddDays(-1), DateRangeEndUtc = received, ReceivedUtc = received, AuthDetailBackfilledUtc = received, Records = [.. records],
        });
        await context.SaveChangesAsync();
    }

    private async Task RunCycleAsync()
    {
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), new FakeAlertWebhookClient(), PsaTestSupport.ForHalo(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);
        await service.CheckPinnedDomainsAsync();
    }

    private async Task<List<AlertEvent>> AlertsAsync(string alertType)
    {
        await using var context = CreateContext();
        return await context.AlertEvents.Where(alert => alert.AlertType == alertType).OrderBy(alert => alert.Id).ToListAsync();
    }

    // ---- New senders ----

    [Fact]
    public async Task ANewUnreviewedSender_RaisesOneAlertPerDomain_NamingTheSenders_AndWhatToDo()
    {
        var domainId = await SeedDomainAsync();
        await AddReportAsync(domainId, 1, Mail(MailchimpIp, 30, "bounce.mcsv.net"), Mail(SendGridIp, 50, "bounce.sendgrid.net"));

        await RunCycleAsync();
        await RunCycleAsync();

        var alert = Assert.Single(await AlertsAsync(AlertTypes.NewUnreviewedSender));
        Assert.Equal(SenderAlertMessages.NewSenders(DomainName, [new("SendGrid", 50), new("Mailchimp", 30)]), alert.Message);
        Assert.Equal((SenderAlertMessages.NewSendersTitle, DomainName, false), (alert.Title, alert.DomainName, alert.IsResolved));
    }

    [Fact]
    public async Task AnInstallWithOnlyOldSenders_RaisesNoNewSenderAlert()
    {
        var domainId = await SeedDomainAsync();
        await AddReportAsync(domainId, 20, Mail(MailchimpIp, 500, "bounce.mcsv.net"));

        await RunCycleAsync();

        Assert.Empty(await AlertsAsync(AlertTypes.NewUnreviewedSender));
    }

    [Fact]
    public async Task ASourceReappearingAfterAQuietSpell_IsNotNew()
    {
        var domainId = await SeedDomainAsync();
        await AddReportAsync(domainId, 60, Mail(MailchimpIp, 500, "bounce.mcsv.net"));
        await AddReportAsync(domainId, 1, Mail(MailchimpIp, 500, "bounce.mcsv.net"));

        await RunCycleAsync();

        Assert.Empty(await AlertsAsync(AlertTypes.NewUnreviewedSender));
    }

    [Theory]
    [InlineData("domain")]
    [InlineData("group")]
    [InlineData("baseline")]
    [InlineData("review")]
    public async Task ADecisionAtAnyLevel_ResolvesTheNewSenderAlert_OnTheNextCycle(string level)
    {
        var domainId = await SeedDomainAsync(groupName: "Aurora Retail");
        await AddReportAsync(domainId, 1, Mail(MailchimpIp, 40, "bounce.mcsv.net"));
        await RunCycleAsync();
        Assert.False(Assert.Single(await AlertsAsync(AlertTypes.NewUnreviewedSender)).IsResolved);

        await using (var context = CreateContext())
        {
            var groupId = (await context.Groups.SingleAsync()).Id;
            switch (level)
            {
                case "domain":
                    await SenderDecisionService.SetAsync(context, TestActors.Admin, domainId, SenderDecisionKind.Service, "Mailchimp", SenderDecisionStatus.Authorised, null);
                    break;
                case "group":
                    await SenderDecisionService.SetForGroupAsync(context, TestActors.Admin, groupId, "Mailchimp", SenderDecisionStatus.Authorised, null);
                    break;
                case "baseline":
                    await SenderDecisionService.SetBaselineAsync(context, TestActors.Admin, "Mailchimp", SenderDecisionStatus.Authorised, null);
                    break;
                default:
                    await SenderDecisionService.SetAsync(context, TestActors.Admin, domainId, SenderDecisionKind.Service, "Mailchimp", SenderDecisionStatus.UnderReview, null);
                    break;
            }
        }

        await RunCycleAsync();

        Assert.True(Assert.Single(await AlertsAsync(AlertTypes.NewUnreviewedSender)).IsResolved);
    }

    // ---- Unauthorised mail delivered ----

    private async Task MarkMailchimpUnauthorisedAsync(int domainId)
    {
        await using var context = CreateContext();
        await SenderDecisionService.SetAsync(context, TestActors.Admin, domainId, SenderDecisionKind.Service, "Mailchimp", SenderDecisionStatus.Unauthorised, null);
    }

    [Fact]
    public async Task AnUnauthorisedSendersDeliveredMail_RaisesAnAlert_WithThePolicyLine()
    {
        var domainId = await SeedDomainAsync(policy: DmarcPolicyLevel.None);
        await AddReportAsync(domainId, 20, Mail(MailchimpIp, 500, "bounce.mcsv.net", passing: false));
        await MarkMailchimpUnauthorisedAsync(domainId);

        await RunCycleAsync();

        var alert = Assert.Single(await AlertsAsync(AlertTypes.UnauthorisedSenderDelivered));
        Assert.Equal(SenderAlertMessages.UnauthorisedDelivered(DomainName, [new("Mailchimp", 500)], DmarcPolicyLevel.None), alert.Message);
        Assert.Equal(SenderAlertMessages.UnauthorisedDeliveredTitle, alert.Title);
    }

    [Fact]
    public async Task TheDeliveredAlert_ResolvesWhenTheVolumeDropsBelowTheThreshold()
    {
        var domainId = await SeedDomainAsync();
        await AddReportAsync(domainId, 20, Mail(MailchimpIp, 50, "bounce.mcsv.net", passing: false));
        await MarkMailchimpUnauthorisedAsync(domainId);
        await RunCycleAsync();
        Assert.False(Assert.Single(await AlertsAsync(AlertTypes.UnauthorisedSenderDelivered)).IsResolved);

        await SaveSettingsAsync(deliveredMinVolume: 1000);
        await RunCycleAsync();

        Assert.True(Assert.Single(await AlertsAsync(AlertTypes.UnauthorisedSenderDelivered)).IsResolved);
    }

    [Fact]
    public async Task BlockedMailFromAnUnauthorisedSender_RaisesNoAlert()
    {
        var domainId = await SeedDomainAsync();
        await AddReportAsync(domainId, 20, Mail(MailchimpIp, 500, "bounce.mcsv.net", DispositionResult.Reject, passing: false));
        await MarkMailchimpUnauthorisedAsync(domainId);

        await RunCycleAsync();

        Assert.Empty(await AlertsAsync(AlertTypes.UnauthorisedSenderDelivered));
    }

    // ---- Switches, settings and null-routed domains ----

    [Fact]
    public async Task TurningEachAlertOff_ResolvesAnOpenOne()
    {
        var domainId = await SeedDomainAsync();
        await AddReportAsync(domainId, 1, Mail(MailchimpIp, 50, "bounce.mcsv.net", passing: false), Mail(SendGridIp, 60, "bounce.sendgrid.net"));
        await MarkMailchimpUnauthorisedAsync(domainId);
        await RunCycleAsync();
        Assert.False(Assert.Single(await AlertsAsync(AlertTypes.NewUnreviewedSender)).IsResolved);
        Assert.False(Assert.Single(await AlertsAsync(AlertTypes.UnauthorisedSenderDelivered)).IsResolved);

        await SaveSettingsAsync(newSenderEnabled: false, deliveredEnabled: false);
        await RunCycleAsync();

        Assert.True(Assert.Single(await AlertsAsync(AlertTypes.NewUnreviewedSender)).IsResolved);
        Assert.True(Assert.Single(await AlertsAsync(AlertTypes.UnauthorisedSenderDelivered)).IsResolved);
    }

    [Fact]
    public async Task ANullRoutedDomain_GetsNeitherAlert()
    {
        var domainId = await SeedDomainAsync(spf: SpfCheckStatus.NullSpf);
        await AddReportAsync(domainId, 1, Mail(MailchimpIp, 50, "bounce.mcsv.net", passing: false), Mail(SendGridIp, 60, "bounce.sendgrid.net"));
        await MarkMailchimpUnauthorisedAsync(domainId);

        await RunCycleAsync();

        Assert.Empty(await AlertsAsync(AlertTypes.NewUnreviewedSender));
        Assert.Empty(await AlertsAsync(AlertTypes.UnauthorisedSenderDelivered));
    }

    [Fact]
    public async Task TheNewDaysAndTheMinimumVolume_AreReadFromTheRule()
    {
        var domainId = await SeedDomainAsync();
        await AddReportAsync(domainId, 3, Mail(MailchimpIp, 30, "bounce.mcsv.net"));

        await SaveSettingsAsync(newSenderDays: 2);
        await RunCycleAsync();
        Assert.Empty(await AlertsAsync(AlertTypes.NewUnreviewedSender));

        await SaveSettingsAsync(newSenderDays: 7, newSenderMinVolume: 100);
        await RunCycleAsync();
        Assert.Empty(await AlertsAsync(AlertTypes.NewUnreviewedSender));

        await SaveSettingsAsync(newSenderDays: 7, newSenderMinVolume: 20);
        await RunCycleAsync();
        Assert.Single(await AlertsAsync(AlertTypes.NewUnreviewedSender));
    }

    [Fact]
    public async Task AnAlignedMicrosoftSender_ThatIsNew_IsNamedAsMicrosoft365()
    {
        var domainId = await SeedDomainAsync();
        var record = new ReportRecord { SourceIp = "40.107.0.1", MessageCount = 60, SpfResult = AuthResult.Pass, DkimResult = AuthResult.Pass, HeaderFrom = DomainName };
        record.AuthDetails.Add(new ReportRecordAuthDetail { Mechanism = DmarcAuthMechanism.Dkim, Domain = DomainName, Result = DmarcMechanismResult.Pass, Selector = "selector1" });
        await AddReportAsync(domainId, 1, record);
        await using (var context = CreateContext())
        {
            context.IpInfos.Add(new IpInfo { Ip = "40.107.0.1", Organization = "Microsoft Corporation", Status = IpLookupStatus.Ok, LookedUpUtc = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
        }

        await RunCycleAsync();

        Assert.Contains("Microsoft 365 (60 messages)", Assert.Single(await AlertsAsync(AlertTypes.NewUnreviewedSender)).Message);
    }

    // ---- The owner lookup ----

    [Fact]
    public void OwnerByIp_UsesTheCurrentIpResult_ThenARange_ThenNull()
    {
        var now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        var cached = new Dictionary<string, IpInfo>
        {
            ["203.0.113.5"] = new() { Ip = "203.0.113.5", Organization = "Own Result Ltd", Status = IpLookupStatus.Ok, LookedUpUtc = now.AddDays(-100) },
            ["203.0.113.6"] = new() { Ip = "203.0.113.6", Organization = null, Status = IpLookupStatus.LookupFailed, LookedUpUtc = now.AddDays(-2) },
        };
        var ranges = new List<IpRange> { new() { RangeStart = "203.0.113.0", RangeEnd = "203.0.113.255", Organization = "Range Owner Ltd" } };

        var owners = IpInfoService.OwnerByIp(cached, ranges, ["203.0.113.5", "203.0.113.6", "203.0.113.7", "192.0.2.1"], now);

        Assert.Equal("Own Result Ltd", owners["203.0.113.5"]);
        Assert.Equal("Range Owner Ltd", owners["203.0.113.6"]);
        Assert.Equal("Range Owner Ltd", owners["203.0.113.7"]);
        Assert.Null(owners["192.0.2.1"]);
    }

}
