using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class SenderDecisionSchemaTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public SenderDecisionSchemaTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private async Task<(int DomainId, int GroupId)> SeedOwnersAsync(string domainName = "example.com")
    {
        await using var context = CreateContext();
        var group = new Group { Name = $"Group for {domainName}" };
        var domain = new Domain { Name = domainName, FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = true, Groups = [group] };
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
        return (domain.Id, group.Id);
    }

    private static SenderDecision Decision(
        SenderDecisionScope scope, int? groupId, int? domainId, SenderDecisionKind kind = SenderDecisionKind.Service, string key = "Mailchimp") => new()
    {
        Scope = scope, GroupId = groupId, DomainId = domainId, Kind = kind, Key = key,
        Status = SenderDecisionStatus.Unauthorised, DecidedBy = "user-1", DecidedByName = "Sam Lee", DecidedUtc = DateTimeOffset.UtcNow,
    };

    private async Task SaveAsync(params SenderDecision[] decisions)
    {
        await using var context = CreateContext();
        context.SenderDecisions.AddRange(decisions);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task TheMigrationApplies_AndSavesADomainDecision()
    {
        var (domainId, _) = await SeedOwnersAsync();

        await SaveAsync(Decision(SenderDecisionScope.Domain, null, domainId));

        await using var context = CreateContext();
        var saved = await context.SenderDecisions.SingleAsync();
        Assert.Equal(("Mailchimp", SenderDecisionStatus.Unauthorised, "Sam Lee"), (saved.Key, saved.Status, saved.DecidedByName));
    }

    [Fact]
    public async Task TwoBaselineDecisionsForTheSameService_AreRefused()
    {
        await SaveAsync(Decision(SenderDecisionScope.Baseline, null, null));

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(Decision(SenderDecisionScope.Baseline, null, null)));
    }

    [Fact]
    public async Task TheSameServiceOnTwoDomains_IsAllowed()
    {
        var (first, _) = await SeedOwnersAsync("first.example");
        var (second, _) = await SeedOwnersAsync("second.example");

        await SaveAsync(Decision(SenderDecisionScope.Domain, null, first), Decision(SenderDecisionScope.Domain, null, second));

        await using var context = CreateContext();
        Assert.Equal(2, await context.SenderDecisions.CountAsync());
    }

    [Fact]
    public async Task AGroupDecisionWithoutAGroup_IsRefused() =>
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(Decision(SenderDecisionScope.Group, null, null)));

    [Fact]
    public async Task ADomainDecisionWithoutADomain_IsRefused() =>
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(Decision(SenderDecisionScope.Domain, null, null)));

    [Fact]
    public async Task ABaselineDecisionWithAnOwner_IsRefused()
    {
        var (domainId, _) = await SeedOwnersAsync();

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(Decision(SenderDecisionScope.Baseline, null, domainId)));
    }

    [Fact]
    public async Task AnIpDecisionAtBaselineOrGroupScope_IsRefused()
    {
        var (_, groupId) = await SeedOwnersAsync();

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(Decision(SenderDecisionScope.Baseline, null, null, SenderDecisionKind.Ip, "203.0.113.9")));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(Decision(SenderDecisionScope.Group, groupId, null, SenderDecisionKind.Ip, "203.0.113.9")));
    }

    [Fact]
    public async Task DeletingADomain_DeletesItsDecisions()
    {
        var (domainId, _) = await SeedOwnersAsync();
        await SaveAsync(Decision(SenderDecisionScope.Domain, null, domainId));

        await using (var context = CreateContext())
        {
            context.Domains.Remove(await context.Domains.SingleAsync(domain => domain.Id == domainId));
            await context.SaveChangesAsync();
        }

        await using var verify = CreateContext();
        Assert.Empty(await verify.SenderDecisions.ToListAsync());
    }

    [Fact]
    public async Task DeletingAGroup_DeletesItsDecisions_ButNotTheBaseline()
    {
        var (_, groupId) = await SeedOwnersAsync();
        await SaveAsync(Decision(SenderDecisionScope.Group, groupId, null), Decision(SenderDecisionScope.Baseline, null, null));

        await using (var context = CreateContext())
        {
            context.Groups.Remove(await context.Groups.SingleAsync(group => group.Id == groupId));
            await context.SaveChangesAsync();
        }

        await using var verify = CreateContext();
        Assert.Equal(SenderDecisionScope.Baseline, (await verify.SenderDecisions.SingleAsync()).Scope);
    }
}
