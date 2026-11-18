using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class SenderDecisionServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public SenderDecisionServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private async Task<int> SeedDomainAsync(string name = "example.com", params Group[] groups)
    {
        await using var context = CreateContext();
        var domain = new Domain { Name = name, FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = true, Groups = [.. groups] };
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
        return domain.Id;
    }

    [Fact]
    public async Task SetAsync_CreatesADomainDecision_WithTheActorsNameAndKey_AndAuditsIt()
    {
        var domainId = await SeedDomainAsync();
        await using (var context = CreateContext())
        {
            await SenderDecisionService.SetAsync(context, TestActors.Admin, domainId, SenderDecisionKind.Service, "Mailchimp", SenderDecisionStatus.Unauthorised, "Not ours");
        }

        await using var verify = CreateContext();
        var decision = await verify.SenderDecisions.SingleAsync();
        Assert.Equal((SenderDecisionScope.Domain, domainId, "Mailchimp", SenderDecisionStatus.Unauthorised, "Not ours"),
            (decision.Scope, decision.DomainId, decision.Key, decision.Status, decision.Note));
        Assert.Equal(("00000000-0000-0000-0000-00000000000a", "Test Admin"), (decision.DecidedBy, decision.DecidedByName));
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.SenderDecisionChanged, entry.Action);
        Assert.Equal("example.com", entry.TargetName);
        Assert.Contains(entry.Changes, change => change is { Field: "Status", Old: "Unreviewed", New: "Unauthorised" });
    }

    [Fact]
    public async Task SetAsync_ChangesAnExistingDecision_AndAuditsTheOldAndNewStatus()
    {
        var domainId = await SeedDomainAsync();
        await using (var context = CreateContext())
        {
            await SenderDecisionService.SetAsync(context, TestActors.Admin, domainId, SenderDecisionKind.Service, "Mailchimp", SenderDecisionStatus.UnderReview, null);
        }

        await using (var context = CreateContext())
        {
            await SenderDecisionService.SetAsync(context, TestActors.Admin, domainId, SenderDecisionKind.Service, "Mailchimp", SenderDecisionStatus.Authorised, null);
        }

        await using var verify = CreateContext();
        Assert.Equal(SenderDecisionStatus.Authorised, (await verify.SenderDecisions.SingleAsync()).Status);
        var latest = await verify.AuditEntries.OrderByDescending(entry => entry.Id).FirstAsync();
        Assert.Contains(latest.Changes, change => change is { Field: "Status", Old: "Under review", New: "Authorised" });
    }

    [Fact]
    public async Task SetAsync_WithNothingChanged_WritesNoAuditEntry()
    {
        var domainId = await SeedDomainAsync();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var context = CreateContext();
            await SenderDecisionService.SetAsync(context, TestActors.Admin, domainId, SenderDecisionKind.Service, "Mailchimp", SenderDecisionStatus.Authorised, "Fine");
        }

        await using var verify = CreateContext();
        Assert.Equal(1, await verify.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task SetAsync_RefusesANoteOverFiveHundredCharacters_AndAKeyOverTwoHundred_AndABlankKey()
    {
        var domainId = await SeedDomainAsync();
        await using var context = CreateContext();

        await Assert.ThrowsAsync<ArgumentException>(() => SenderDecisionService.SetAsync(
            context, TestActors.Admin, domainId, SenderDecisionKind.Service, "Mailchimp", SenderDecisionStatus.Authorised, new string('x', 501)));
        await Assert.ThrowsAsync<ArgumentException>(() => SenderDecisionService.SetAsync(
            context, TestActors.Admin, domainId, SenderDecisionKind.Service, new string('k', 201), SenderDecisionStatus.Authorised, null));
        await Assert.ThrowsAsync<ArgumentException>(() => SenderDecisionService.SetAsync(
            context, TestActors.Admin, domainId, SenderDecisionKind.Service, "  ", SenderDecisionStatus.Authorised, null));
    }

    [Fact]
    public async Task SetAsync_ForAnUnknownDomain_Throws()
    {
        await using var context = CreateContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SenderDecisionService.SetAsync(
            context, TestActors.Admin, 424242, SenderDecisionKind.Service, "Mailchimp", SenderDecisionStatus.Authorised, null));
    }

    [Fact]
    public async Task ResetAsync_DeletesTheDecision_AndAudits_ButDoesNothingWhenThereIsNone()
    {
        var domainId = await SeedDomainAsync();
        await using (var context = CreateContext())
        {
            await SenderDecisionService.ResetAsync(context, TestActors.Admin, domainId, SenderDecisionKind.Service, "Mailchimp");
            await SenderDecisionService.SetAsync(context, TestActors.Admin, domainId, SenderDecisionKind.Service, "Mailchimp", SenderDecisionStatus.Unauthorised, null);
        }

        await using (var context = CreateContext())
        {
            await SenderDecisionService.ResetAsync(context, TestActors.Admin, domainId, SenderDecisionKind.Service, "Mailchimp");
        }

        await using var verify = CreateContext();
        Assert.Empty(await verify.SenderDecisions.ToListAsync());
        var latest = await verify.AuditEntries.OrderByDescending(entry => entry.Id).FirstAsync();
        Assert.Contains(latest.Changes, change => change is { Field: "Status", Old: "Unauthorised", New: "Unreviewed" });
        Assert.Equal(2, await verify.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task TwoCallersSettingTheSameSenderAtOnce_LeaveOneRow_AndBothSucceed()
    {
        var domainId = await SeedDomainAsync();
        await using var first = CreateContext();
        await using var second = CreateContext();

        await Task.WhenAll(
            SenderDecisionService.SetAsync(first, TestActors.Admin, domainId, SenderDecisionKind.Service, "Mailchimp", SenderDecisionStatus.Authorised, null),
            SenderDecisionService.SetAsync(second, TestActors.Admin, domainId, SenderDecisionKind.Service, "Mailchimp", SenderDecisionStatus.Unauthorised, null));

        await using var verify = CreateContext();
        Assert.Single(await verify.SenderDecisions.ToListAsync());
    }

    [Fact]
    public async Task LoadForDomainAsync_ReturnsTheDomainsGroupsAndBaselineDecisions_AndNothingElse()
    {
        var aurora = new Group { Name = "Aurora Retail" };
        var elsewhere = new Group { Name = "Elsewhere" };
        var domainId = await SeedDomainAsync("example.com", aurora);
        var otherDomainId = await SeedDomainAsync("other.example", elsewhere);
        await using (var context = CreateContext())
        {
            var auroraId = (await context.Groups.SingleAsync(group => group.Name == "Aurora Retail")).Id;
            var elsewhereId = (await context.Groups.SingleAsync(group => group.Name == "Elsewhere")).Id;
            SenderDecision Row(SenderDecisionScope scope, int? groupId, int? ownerDomainId, string key) => new()
            {
                Scope = scope, GroupId = groupId, DomainId = ownerDomainId, Kind = SenderDecisionKind.Service, Key = key,
                Status = SenderDecisionStatus.Authorised, DecidedBy = "user-1", DecidedByName = "Sam Lee",
            };
            context.SenderDecisions.AddRange(
                Row(SenderDecisionScope.Baseline, null, null, "Baseline"),
                Row(SenderDecisionScope.Group, auroraId, null, "InGroup"),
                Row(SenderDecisionScope.Group, elsewhereId, null, "OtherGroup"),
                Row(SenderDecisionScope.Domain, null, domainId, "OwnDomain"),
                Row(SenderDecisionScope.Domain, null, otherDomainId, "OtherDomain"));
            await context.SaveChangesAsync();
        }

        await using var read = CreateContext();
        var loaded = await SenderDecisionService.LoadForDomainAsync(read, domainId);

        Assert.Equal(["Baseline", "InGroup", "OwnDomain"], loaded.Select(decision => decision.Key).Order());
    }

    private async Task<int> SeedGroupAsync(string name)
    {
        await using var context = CreateContext();
        var group = new Group { Name = name };
        context.Groups.Add(group);
        await context.SaveChangesAsync();
        return group.Id;
    }

    [Fact]
    public async Task SetBaselineAsync_CreatesABaselineDecision_AuditedAgainstTheSenderBaselineSettings()
    {
        await using (var context = CreateContext())
        {
            await SenderDecisionService.SetBaselineAsync(context, TestActors.Admin, "Microsoft 365", SenderDecisionStatus.Authorised, "Fine everywhere");
        }

        await using var verify = CreateContext();
        var decision = await verify.SenderDecisions.SingleAsync();
        Assert.Equal((SenderDecisionScope.Baseline, null, null, "Microsoft 365", SenderDecisionStatus.Authorised, "Fine everywhere"),
            (decision.Scope, decision.GroupId, decision.DomainId, decision.Key, decision.Status, decision.Note));
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal((AuditActions.SenderDecisionChanged, "Settings", "Sender baseline"), (entry.Action, entry.TargetType, entry.TargetName));
        Assert.Contains(entry.Changes, change => change is { Field: "Status", Old: "Unreviewed", New: "Authorised" });
    }

    [Fact]
    public async Task SetBaselineAsync_ChangesAndResets_AndAuditsEachStep()
    {
        await using (var context = CreateContext())
        {
            await SenderDecisionService.SetBaselineAsync(context, TestActors.Admin, "Mailchimp", SenderDecisionStatus.UnderReview, null);
        }

        await using (var context = CreateContext())
        {
            await SenderDecisionService.SetBaselineAsync(context, TestActors.Admin, "Mailchimp", SenderDecisionStatus.Unauthorised, null);
        }

        await using (var context = CreateContext())
        {
            await SenderDecisionService.ResetBaselineAsync(context, TestActors.Admin, "Mailchimp");
        }

        await using var verify = CreateContext();
        Assert.Empty(await verify.SenderDecisions.ToListAsync());
        var entries = await verify.AuditEntries.OrderBy(entry => entry.Id).ToListAsync();
        Assert.Equal(3, entries.Count);
        Assert.Contains(entries[1].Changes, change => change is { Field: "Status", Old: "Under review", New: "Unauthorised" });
        Assert.Contains(entries[2].Changes, change => change is { Field: "Status", Old: "Unauthorised", New: "Unreviewed" });
    }

    [Fact]
    public async Task SetBaselineAsync_WithAnUnknownServiceName_Throws()
    {
        await using var context = CreateContext();

        await Assert.ThrowsAsync<ArgumentException>(() => SenderDecisionService.SetBaselineAsync(
            context, TestActors.Admin, "Mailchimpp", SenderDecisionStatus.Authorised, null));
    }

    [Fact]
    public async Task SetBaselineAsync_WithNothingChanged_WritesNoAuditEntry()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var context = CreateContext();
            await SenderDecisionService.SetBaselineAsync(context, TestActors.Admin, "SendGrid", SenderDecisionStatus.Authorised, "Same");
        }

        await using var verify = CreateContext();
        Assert.Equal(1, await verify.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task TwoPeopleSettingTheSameBaselineEntryAtOnce_LeaveOneRow_AndBothSucceed()
    {
        await using var first = CreateContext();
        await using var second = CreateContext();

        await Task.WhenAll(
            SenderDecisionService.SetBaselineAsync(first, TestActors.Admin, "Mailchimp", SenderDecisionStatus.Authorised, null),
            SenderDecisionService.SetBaselineAsync(second, TestActors.Admin, "Mailchimp", SenderDecisionStatus.Unauthorised, null));

        await using var verify = CreateContext();
        Assert.Single(await verify.SenderDecisions.ToListAsync());
    }

    [Fact]
    public async Task SetForGroupAsync_CreatesAGroupDecision_AuditedAgainstTheGroup()
    {
        var groupId = await SeedGroupAsync("Aurora Retail");
        await using (var context = CreateContext())
        {
            await SenderDecisionService.SetForGroupAsync(context, TestActors.Admin, groupId, "Mailchimp", SenderDecisionStatus.Unauthorised, null);
        }

        await using var verify = CreateContext();
        var decision = await verify.SenderDecisions.SingleAsync();
        Assert.Equal((SenderDecisionScope.Group, groupId, (int?)null, "Mailchimp"), (decision.Scope, decision.GroupId, decision.DomainId, decision.Key));
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(("Group", "Aurora Retail"), (entry.TargetType, entry.TargetName));
    }

    [Fact]
    public async Task SetForGroupAsync_ForAnUnknownGroup_Throws()
    {
        await using var context = CreateContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SenderDecisionService.SetForGroupAsync(
            context, TestActors.Admin, 424242, "Mailchimp", SenderDecisionStatus.Authorised, null));
    }

    [Fact]
    public async Task ResetForGroupAsync_DeletesOnlyThatGroupsDecision_AndDoesNothingWhenThereIsNone()
    {
        var aurora = await SeedGroupAsync("Aurora Retail");
        var brightline = await SeedGroupAsync("Brightline Legal");
        await using (var context = CreateContext())
        {
            await SenderDecisionService.ResetForGroupAsync(context, TestActors.Admin, aurora, "Mailchimp");
            await SenderDecisionService.SetForGroupAsync(context, TestActors.Admin, aurora, "Mailchimp", SenderDecisionStatus.Authorised, null);
            await SenderDecisionService.SetForGroupAsync(context, TestActors.Admin, brightline, "Mailchimp", SenderDecisionStatus.Unauthorised, null);
        }

        await using (var context = CreateContext())
        {
            await SenderDecisionService.ResetForGroupAsync(context, TestActors.Admin, aurora, "Mailchimp");
        }

        await using var verify = CreateContext();
        Assert.Equal(brightline, (await verify.SenderDecisions.SingleAsync()).GroupId);
        Assert.Equal(3, await verify.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task LoadBaselineAsync_ReturnsOnlyBaselineRows()
    {
        var groupId = await SeedGroupAsync("Aurora Retail");
        await using (var context = CreateContext())
        {
            await SenderDecisionService.SetBaselineAsync(context, TestActors.Admin, "Mailchimp", SenderDecisionStatus.Authorised, null);
            await SenderDecisionService.SetForGroupAsync(context, TestActors.Admin, groupId, "SendGrid", SenderDecisionStatus.Authorised, null);
        }

        await using var read = CreateContext();

        Assert.Equal(["Mailchimp"], (await SenderDecisionService.LoadBaselineAsync(read)).Select(decision => decision.Key));
    }

    [Fact]
    public async Task LoadForGroupAsync_ReturnsTheGroupsOwnDecisionsAndTheBaseline_AndNothingElse()
    {
        var aurora = await SeedGroupAsync("Aurora Retail");
        var brightline = await SeedGroupAsync("Brightline Legal");
        var domainId = await SeedDomainAsync("example.com");
        await using (var context = CreateContext())
        {
            await SenderDecisionService.SetBaselineAsync(context, TestActors.Admin, "Mailchimp", SenderDecisionStatus.Authorised, null);
            await SenderDecisionService.SetForGroupAsync(context, TestActors.Admin, aurora, "SendGrid", SenderDecisionStatus.Authorised, null);
            await SenderDecisionService.SetForGroupAsync(context, TestActors.Admin, brightline, "Zendesk", SenderDecisionStatus.Authorised, null);
            await SenderDecisionService.SetAsync(context, TestActors.Admin, domainId, SenderDecisionKind.Service, "HubSpot", SenderDecisionStatus.Authorised, null);
        }

        await using var read = CreateContext();

        Assert.Equal(["Mailchimp", "SendGrid"], (await SenderDecisionService.LoadForGroupAsync(read, aurora)).Select(decision => decision.Key).Order());
    }

    [Fact]
    public async Task AStoredDecisionForAServiceNoLongerInTheCatalogue_CanStillBeReset()
    {
        await using (var context = CreateContext())
        {
            context.SenderDecisions.Add(new SenderDecision
            {
                Scope = SenderDecisionScope.Baseline, Kind = SenderDecisionKind.Service, Key = "Retired Service",
                Status = SenderDecisionStatus.Authorised, DecidedBy = "user-1", DecidedByName = "Sam Lee",
            });
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            await SenderDecisionService.ResetBaselineAsync(context, TestActors.Admin, "Retired Service");
        }

        await using var verify = CreateContext();
        Assert.Empty(await verify.SenderDecisions.ToListAsync());
    }

    [Fact]
    public async Task TheAdminRole_HasSenderReview_AfterBootstrapping()
    {
        await using var context = CreateContext();

        await AccessBootstrapper.BootstrapWithLeaderLockAsync(
            context, Options.Create(new InitialAdminsOptions { Emails = "" }), NullLogger.Instance, CancellationToken.None);

        await using var verify = CreateContext();
        Assert.Contains(Permission.SenderReview, (await verify.Roles.SingleAsync(role => role.Name == "Admin")).Permissions);
    }
}
