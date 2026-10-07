using System.Security.Claims;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Portal;
using DotMarc.Security;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Portal;

[Collection("Postgres")]
public sealed class PortalDataTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public PortalDataTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private PortalData CreatePortalData() => new(new FakeDbContextFactory(_connectionString), TimeProvider.System);

    private async Task<(int AuroraGroupId, int OtherGroupId)> SeedAsync()
    {
        await using var context = CreateContext();
        var aurora = new Group { Name = "Aurora Retail" };
        var other = new Group { Name = "Brightline Legal" };
        context.Domains.AddRange(
            new Domain { Name = "aurora-retail.example", FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = true, Groups = [aurora], DmarcPolicy = DmarcPolicyLevel.Reject, LastReportReceivedUtc = DateTimeOffset.UtcNow },
            new Domain { Name = "shop.aurora-retail.example", FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = true, Groups = [aurora], SpfCheckStatus = SpfCheckStatus.MissingRecord },
            new Domain { Name = "not-monitored.aurora-retail.example", FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = false, Groups = [aurora] },
            new Domain { Name = "brightline-legal.example", FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = true, Groups = [other] });
        context.AlertEvents.Add(new AlertEvent { DomainName = "shop.aurora-retail.example", AlertType = "SpfRecordBroken", Severity = "Warning", Title = "SPF record broken", Message = "m" });
        await context.SaveChangesAsync();
        return (aurora.Id, other.Id);
    }

    [Fact]
    public async Task ListDomains_ShowsOnlyMonitoredDomainsInTheGivenGroups_AttentionFirst()
    {
        var (auroraGroupId, _) = await SeedAsync();

        var domains = await CreatePortalData().ListDomainsAsync([auroraGroupId]);

        Assert.Equal(["shop.aurora-retail.example", "aurora-retail.example"], domains.Select(domain => domain.Name));
        Assert.Equal(PortalHealth.NeedsAttention, domains[0].Status.Health);
        Assert.Equal("SPF record broken", Assert.Single(domains[0].OpenAlerts).Title);
        Assert.Equal(PortalHealth.Protected, domains[1].Status.Health);
        Assert.Equal(30, domains[1].Trend.Count);
    }

    [Fact]
    public async Task ListDomains_WithNoGroups_ShowsNothing()
    {
        await SeedAsync();

        Assert.Empty(await CreatePortalData().ListDomainsAsync([]));
    }

    [Fact]
    public async Task GetDomain_OutsideTheGroups_IsNull()
    {
        var (auroraGroupId, _) = await SeedAsync();

        Assert.Null(await CreatePortalData().GetDomainAsync([auroraGroupId], "brightline-legal.example"));
        Assert.Null(await CreatePortalData().GetDomainAsync([auroraGroupId], "not-monitored.aurora-retail.example"));
        Assert.NotNull(await CreatePortalData().GetDomainAsync([auroraGroupId], "aurora-retail.example"));
    }

    [Fact]
    public async Task GetDomain_WithNoGroups_IsNull()
    {
        await SeedAsync();

        Assert.Null(await CreatePortalData().GetDomainAsync([], "aurora-retail.example"));
    }

    [Fact]
    public void ScopedGroupIds_WithoutScopeClaims_IsEmpty_NeverEverything()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(UserAccessClaimsTransformation.ClientPortalClaimType, "true")], "Test"));

        Assert.Empty(PortalData.ScopedGroupIds(user));
    }
}
