using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>The Under review setting through the real cycle: a stacked rule can alert for a sender left Under review while the default
/// rule, which counts it as decided, stays quiet.</summary>
[Collection("Postgres")]
public sealed class UnderReviewAlertingTests(PostgresContainerFixture fixture)
{
    private const string MailchimpIp = "198.51.100.7";

    private static ReportRecord MailchimpMail(string domainName)
    {
        var record = new ReportRecord
        {
            SourceIp = MailchimpIp, MessageCount = 30, Disposition = DispositionResult.None, HeaderFrom = domainName, SpfResult = AuthResult.Pass, DkimResult = AuthResult.Fail
        };
        record.AuthDetails.Add(new ReportRecordAuthDetail { Mechanism = DmarcAuthMechanism.Dkim, Domain = "bounce.mcsv.net", Result = DmarcMechanismResult.Pass });
        return record;
    }

    private static Report ReportAt(DateTimeOffset received, ReportRecord record) => new()
    {
        ReportingOrg = "receiver.example", ReportId = Guid.NewGuid().ToString(), RawXml = "<feedback/>", DateRangeBeginUtc = received.AddDays(-1),
        DateRangeEndUtc = received, ReceivedUtc = received, AuthDetailBackfilledUtc = received, Records = [record]
    };

    [Fact]
    public async Task ARuleWithAfterDays_RaisesAgainOnceTheWindowPasses_WhileTheDefaultStaysQuiet()
    {
        var (connectionString, cleanup) = await fixture.CreateDatabaseAsync();
        await using (cleanup)
        {
            var options = new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connectionString).Options;
            int stackedRuleId;
            await using (var context = new DotMarcDbContext(options))
            {
                await context.Database.MigrateAsync();
                await NotificationSettingsService.SaveAsync(context, TestActors.Admin, new NotificationSettings { Enabled = true, TeamsEnabled = true, TeamsWebhookUrl = "https://example.test/webhook" });
                await AlertRuleSeeder.EnsureDefaultPackAsync(context, CancellationToken.None);
                var stacked = new AlertRule
                {
                    Kind = AlertTypes.NewUnreviewedSender, Name = "Nag about reviews", Severity = "Warning", Position = -10, StopProcessing = false,
                    ParametersJson = "{\"underReview\":\"afterDays\",\"underReviewDays\":\"14\"}"
                };
                context.AlertRules.Add(stacked);

                // First seen 20 days ago, so it is long past new, and put Under review 20 days ago too.
                context.Domains.Add(new Domain
                {
                    Name = "nag.test", IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-60), LastReportReceivedUtc = DateTimeOffset.UtcNow.AddDays(-1),
                    Reports = [ReportAt(DateTimeOffset.UtcNow.AddDays(-20), MailchimpMail("nag.test")), ReportAt(DateTimeOffset.UtcNow.AddDays(-1), MailchimpMail("nag.test"))]
                });
                context.SenderDecisions.Add(new SenderDecision
                {
                    Scope = SenderDecisionScope.Baseline, Kind = SenderDecisionKind.Service, Key = "Mailchimp", Status = SenderDecisionStatus.UnderReview,
                    DecidedBy = "u", DecidedByName = "Sam Lee", DecidedUtc = DateTimeOffset.UtcNow.AddDays(-20)
                });
                await context.SaveChangesAsync();
                stackedRuleId = stacked.Id;
            }

            var service = new AlertingService(new FakeDbContextFactory(connectionString), new FakeAlertWebhookClient(), PsaTestSupport.ForHalo(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);
            await service.CheckPinnedDomainsAsync();

            await using var verify = new DotMarcDbContext(options);
            var alert = Assert.Single(await verify.AlertEvents.Where(candidate => candidate.AlertType == AlertTypes.NewUnreviewedSender).ToListAsync());
            Assert.Equal(stackedRuleId, alert.AlertRuleId);
            Assert.Contains("still under review: Mailchimp", alert.Message);
        }
    }
}
