using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class DomainAlertStateTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DomainAlertStateTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    [Fact]
    public async Task AStateRow_IsSaved_AndOnlyOnePerDomainAndItem()
    {
        await using (var context = CreateContext())
        {
            var domain = new Domain { Name = "contoso.com", FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = true };
            domain.AlertStates.Add(new DomainAlertState { Item = "Spf", HasPassed = true });
            domain.AlertStates.Add(new DomainAlertState { Item = "DmarcPolicy", Baseline = "p=reject; sp=reject; pct=100" });
            context.Domains.Add(domain);
            await context.SaveChangesAsync();
        }

        await using (var verify = CreateContext())
        {
            var states = await verify.DomainAlertStates.OrderBy(state => state.Item).ToListAsync();
            Assert.Equal(["DmarcPolicy", "Spf"], states.Select(state => state.Item));
            Assert.Equal("p=reject; sp=reject; pct=100", states[0].Baseline);
            Assert.True(states[1].HasPassed);
        }

        await using var duplicate = CreateContext();
        var domainId = (await duplicate.Domains.SingleAsync()).Id;
        duplicate.DomainAlertStates.Add(new DomainAlertState { DomainId = domainId, Item = "Spf" });
        await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
    }

    [Fact]
    public async Task DeletingADomain_DeletesItsStateRows()
    {
        await using (var context = CreateContext())
        {
            var domain = new Domain { Name = "contoso.com", FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = true };
            domain.AlertStates.Add(new DomainAlertState { Item = "Mx", HasPassed = true });
            context.Domains.Add(domain);
            await context.SaveChangesAsync();
            context.Domains.Remove(domain);
            await context.SaveChangesAsync();
        }

        await using var verify = CreateContext();
        Assert.Empty(verify.DomainAlertStates);
    }

    [Fact]
    public async Task ADomainsDmarcPolicyFields_AreSaved()
    {
        await using (var context = CreateContext())
        {
            context.Domains.Add(new Domain
            {
                Name = "contoso.com", FirstSeenUtc = DateTimeOffset.UtcNow,
                DmarcPolicy = DmarcPolicyLevel.Quarantine, DmarcSubdomainPolicy = DmarcPolicyLevel.None, DmarcPercent = 50
            });
            await context.SaveChangesAsync();
        }

        await using var verify = CreateContext();
        var saved = await verify.Domains.SingleAsync();
        Assert.Equal((DmarcPolicyLevel.Quarantine, DmarcPolicyLevel.None, 50), (saved.DmarcPolicy!.Value, saved.DmarcSubdomainPolicy!.Value, saved.DmarcPercent!.Value));
    }
}
