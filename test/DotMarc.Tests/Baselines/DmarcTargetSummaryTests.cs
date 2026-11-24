using System.Globalization;
using System.Security.Claims;
using DotMarc.Baselines;
using DotMarc.Data;
using DotMarc.Security;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Baselines;

/// <summary>What the Baselines Domain tab shows, and how it narrows for a person limited to some groups.</summary>
[Collection("Postgres")]
public sealed class DmarcTargetSummaryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset CheckedAt = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DmarcTarget Reject = new(DmarcPolicyLevel.Reject, null);

    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DmarcTargetSummaryTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static ClaimsPrincipal Staff(params int[] scopedGroupIds) =>
        new(new ClaimsIdentity(
            scopedGroupIds.Select(groupId => new Claim(UserAccessClaimsTransformation.ScopedGroupClaimType, groupId.ToString(CultureInfo.InvariantCulture))),
            "Test"));

    private static Domain CheckedDomain(string name, bool monitored, DmarcPolicyLevel policy, params Group[] groups) => new()
    {
        Name = name,
        FirstSeenUtc = CheckedAt,
        IsMonitored = monitored,
        DmarcCheckStatus = DmarcCheckStatus.Ok,
        DmarcCheckedUtc = CheckedAt,
        DmarcPolicy = policy,
        DmarcSubdomainPolicy = policy,
        DmarcPercent = 100,
        Groups = [.. groups]
    };

    private async Task SetSystemTargetAsync(DmarcTarget target)
    {
        await using var context = CreateContext();
        await DmarcTargetService.SetSystemAsync(context, TestActors.Admin, target, CancellationToken.None);
    }

    [Fact]
    public async Task Counts_OnlyMonitoredDomains_BelowAndNeedingChecking()
    {
        await SetSystemTargetAsync(Reject);
        await using (var seed = CreateContext())
        {
            seed.Domains.AddRange(
                CheckedDomain("below.example", monitored: true, DmarcPolicyLevel.None),
                CheckedDomain("meets.example", monitored: true, DmarcPolicyLevel.Reject),
                CheckedDomain("unmonitored-below.example", monitored: false, DmarcPolicyLevel.None),
                new Domain { Name = "unchecked.example", FirstSeenUtc = CheckedAt, IsMonitored = true },
                new Domain { Name = "unmonitored-unchecked.example", FirstSeenUtc = CheckedAt, IsMonitored = false });
            await seed.SaveChangesAsync();
        }

        await using var context = CreateContext();
        var summary = await DmarcTargetSummary.LoadAsync(context, Staff(), CancellationToken.None);

        Assert.Equal(1, summary.BelowCount);
        Assert.Equal(1, summary.NeedsCheckingCount);
        Assert.Equal(Reject, summary.System);
    }

    [Fact]
    public async Task GroupRows_CountMembers_AndCarryTheirTarget()
    {
        await using (var seed = CreateContext())
        {
            var contoso = new Group { Name = "Contoso", TargetDmarcPolicy = DmarcPolicyLevel.Quarantine };
            var fabrikam = new Group { Name = "Fabrikam" };
            seed.Domains.AddRange(
                CheckedDomain("a.example", monitored: true, DmarcPolicyLevel.Reject, contoso),
                CheckedDomain("b.example", monitored: false, DmarcPolicyLevel.Reject, contoso, fabrikam));
            await seed.SaveChangesAsync();
        }

        await using var context = CreateContext();
        var summary = await DmarcTargetSummary.LoadAsync(context, Staff(), CancellationToken.None);

        Assert.Equal(
            [("Contoso", 2, "p=quarantine"), ("Fabrikam", 1, "not set")],
            summary.Groups.Select(group => (group.Name, group.DomainCount, DmarcTargetText.Stored(group.Target))));
    }

    [Fact]
    public async Task AGroupLimitedPerson_SeesOnlyTheirGroupsOverridesAndCounts()
    {
        await SetSystemTargetAsync(Reject);
        int contosoId;
        await using (var seed = CreateContext())
        {
            var contoso = new Group { Name = "Contoso" };
            var fabrikam = new Group { Name = "Fabrikam" };
            var ownBelow = CheckedDomain("own-below.example", monitored: true, DmarcPolicyLevel.None, contoso);
            ownBelow.TargetDmarcPolicy = DmarcPolicyLevel.Quarantine;
            var otherBelow = CheckedDomain("other-below.example", monitored: true, DmarcPolicyLevel.None, fabrikam);
            otherBelow.TargetDmarcPolicy = DmarcPolicyLevel.Reject;
            seed.Domains.AddRange(ownBelow, otherBelow);
            await seed.SaveChangesAsync();
            contosoId = contoso.Id;
        }

        await using var context = CreateContext();
        var summary = await DmarcTargetSummary.LoadAsync(context, Staff(contosoId), CancellationToken.None);

        Assert.Equal(["Contoso"], summary.Groups.Select(group => group.Name));
        Assert.Equal(["own-below.example"], summary.Overrides.Select(row => row.Name));
        Assert.Equal(1, summary.BelowCount);
    }
}
