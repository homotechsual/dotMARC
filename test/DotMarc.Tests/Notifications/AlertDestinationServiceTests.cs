using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class AlertDestinationServiceTests : IAsyncLifetime
{
    private const string SecretUrl = "https://example.test/hooks/very-secret-token";

    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AlertDestinationServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
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

    private static AlertDestinationInput Input(string name = "Aurora Teams", AlertChannel kind = AlertChannel.Teams, string url = SecretUrl, bool enabled = true) =>
        new(name, kind, url, enabled);

    private async Task<AlertDestination> CreateAsync(AlertDestinationInput input)
    {
        await using var context = CreateContext();
        return await AlertDestinationService.CreateAsync(context, TestActors.Admin, input);
    }

    private async Task<List<AuditEntry>> AuditAsync(string action)
    {
        await using var context = CreateContext();
        return await context.AuditEntries.AsNoTracking().Where(entry => entry.Action == action).OrderBy(entry => entry.Id).ToListAsync();
    }

    private async Task SelectInRuleAsync(string ruleKind, params int[] destinationIds)
    {
        await AlertRuleTestSupport.EditDefaultRuleAsync(_connectionString, ruleKind, rule =>
        {
            rule.DestinationMode = AlertDestinationMode.Selected;
            rule.Destinations.AddRange(destinationIds.Select(id => new AlertRuleDestination { AlertDestinationId = id }));
        });
    }

    [Fact]
    public async Task Create_StoresTheDestination_AndAuditsItWithoutTheUrl()
    {
        var created = await CreateAsync(Input(name: "  Aurora Teams "));

        await using var context = CreateContext();
        var stored = await context.AlertDestinations.AsNoTracking().SingleAsync(destination => destination.Id == created.Id);
        Assert.Equal(("Aurora Teams", AlertChannel.Teams, SecretUrl, true), (stored.Name, stored.Kind, stored.Url, stored.Enabled));
        var entry = Assert.Single(await AuditAsync(AuditActions.AlertDestinationCreated));
        Assert.Equal("Aurora Teams", entry.TargetName);
        Assert.DoesNotContain("very-secret-token", entry.Summary);
        Assert.DoesNotContain(entry.Changes, change => (change.Old ?? "").Contains("very-secret-token") || (change.New ?? "").Contains("very-secret-token"));
    }

    [Theory]
    [InlineData("", "Name")]
    [InlineData("   ", "Name")]
    public async Task Create_RefusesABlankName(string name, string field)
    {
        await using var context = CreateContext();

        var exception = await Assert.ThrowsAsync<AlertDestinationException>(() => AlertDestinationService.CreateAsync(context, TestActors.Admin, Input(name: name)));

        Assert.Contains(field, exception.Message);
        Assert.Empty(await AuditAsync(AuditActions.AlertDestinationCreated));
    }

    [Fact]
    public async Task Create_RefusesANameOver100Characters()
    {
        await using var context = CreateContext();

        var exception = await Assert.ThrowsAsync<AlertDestinationException>(() => AlertDestinationService.CreateAsync(context, TestActors.Admin, Input(name: new string('n', 101))));

        Assert.Contains("100", exception.Message);
    }

    [Fact]
    public async Task Create_RefusesADuplicateName_IgnoringCase()
    {
        await CreateAsync(Input(name: "Aurora Teams"));
        await using var context = CreateContext();

        var exception = await Assert.ThrowsAsync<AlertDestinationException>(() => AlertDestinationService.CreateAsync(context, TestActors.Admin, Input(name: "aurora teams")));

        Assert.Contains("already", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://example.test/insecure")]
    [InlineData("https://user:password@example.test/hook")]
    public async Task Create_RefusesAMissingOrUnsafeUrl_WithoutQuotingIt(string url)
    {
        await using var context = CreateContext();

        var exception = await Assert.ThrowsAsync<AlertDestinationException>(() => AlertDestinationService.CreateAsync(context, TestActors.Admin, Input(url: url)));

        Assert.Contains("URL", exception.Message);
        Assert.DoesNotContain("password", exception.Message);
    }

    [Fact]
    public async Task Update_AuditsEachChangedField_AndTheUrlOnlyAsASecret()
    {
        var created = await CreateAsync(Input());

        await using (var context = CreateContext())
        {
            await AlertDestinationService.UpdateAsync(context, TestActors.Admin, created.Id, Input(name: "Renamed", kind: AlertChannel.Slack, url: "https://hooks.slack.com/services/T0/B0/other-secret", enabled: false));
        }

        var entry = Assert.Single(await AuditAsync(AuditActions.AlertDestinationChanged));
        Assert.Contains(entry.Changes, change => change is { Field: "Name", Old: "Aurora Teams", New: "Renamed" });
        Assert.Contains(entry.Changes, change => change is { Field: "Kind", Old: "Teams", New: "Slack" });
        Assert.Contains(entry.Changes, change => change.Field == "Enabled");
        Assert.Contains(entry.Changes, change => change is { Field: "Webhook URL", Secret: true });
        Assert.DoesNotContain(entry.Changes, change => (change.Old ?? "").Contains("secret") || (change.New ?? "").Contains("secret"));
    }

    [Fact]
    public async Task Update_WithNothingChanged_WritesNoAuditEntry()
    {
        var created = await CreateAsync(Input());

        await using (var context = CreateContext())
        {
            await AlertDestinationService.UpdateAsync(context, TestActors.Admin, created.Id, Input());
        }

        Assert.Empty(await AuditAsync(AuditActions.AlertDestinationChanged));
    }

    [Fact]
    public async Task Update_AllowsKeepingItsOwnName()
    {
        var created = await CreateAsync(Input(name: "Aurora Teams"));

        await using var context = CreateContext();
        await AlertDestinationService.UpdateAsync(context, TestActors.Admin, created.Id, Input(name: "AURORA TEAMS", enabled: false));

        Assert.Equal("AURORA TEAMS", (await context.AlertDestinations.AsNoTracking().SingleAsync()).Name);
    }

    [Fact]
    public async Task RuleCount_CountsOnlySelectedRulesThatLinkTheDestination()
    {
        var destination = await CreateAsync(Input());
        await SelectInRuleAsync(AlertTypes.MissedReport, destination.Id);
        await AlertRuleTestSupport.EditDefaultRuleAsync(_connectionString, AlertTypes.SpfRecordBroken, rule => rule.Destinations.Add(new AlertRuleDestination { AlertDestinationId = destination.Id }));

        await using var context = CreateContext();

        Assert.Equal(1, await AlertDestinationService.CountRulesAsync(context, destination.Id));
    }

    [Fact]
    public async Task Delete_RemovesTheDestinationAndItsLinks_AndAuditsIt()
    {
        var destination = await CreateAsync(Input());
        await SelectInRuleAsync(AlertTypes.MissedReport, destination.Id);

        await using (var context = CreateContext())
        {
            await AlertDestinationService.DeleteAsync(context, TestActors.Admin, destination.Id);
        }

        await using var verify = CreateContext();
        Assert.Empty(await verify.AlertDestinations.ToListAsync());
        Assert.Empty(await verify.AlertRuleDestinations.ToListAsync());
        Assert.Single(await AuditAsync(AuditActions.AlertDestinationDeleted));
    }

    [Fact]
    public async Task DeletingTheOnlyDestinationARuleSelects_LeavesTheRuleSelectedAndQuiet()
    {
        var destination = await CreateAsync(Input());
        await SelectInRuleAsync(AlertTypes.MissedReport, destination.Id);

        await using (var context = CreateContext())
        {
            await AlertDestinationService.DeleteAsync(context, TestActors.Admin, destination.Id);
        }

        await using var verify = CreateContext();
        var rule = await verify.AlertRules.AsNoTracking().Include(candidate => candidate.Destinations).SingleAsync(candidate => candidate.DefaultKey == AlertTypes.MissedReport);
        Assert.Equal(AlertDestinationMode.Selected, rule.DestinationMode);
        Assert.Empty(rule.Destinations);
    }

    [Fact]
    public async Task UpdatingOrDeletingAMissingDestination_SaysSo()
    {
        await using var context = CreateContext();

        await Assert.ThrowsAsync<AlertDestinationException>(() => AlertDestinationService.UpdateAsync(context, TestActors.Admin, 999, Input()));
        await Assert.ThrowsAsync<AlertDestinationException>(() => AlertDestinationService.DeleteAsync(context, TestActors.Admin, 999));
    }
}
