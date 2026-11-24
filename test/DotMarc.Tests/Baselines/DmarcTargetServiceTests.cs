using DotMarc.Audit;
using DotMarc.Baselines;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Baselines;

[Collection("Postgres")]
public sealed class DmarcTargetServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DmarcTargetServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private async Task<(int GroupId, int DomainId)> SeedGroupAndDomainAsync()
    {
        await using var context = CreateContext();
        var group = new Group { Name = "Contoso" };
        var domain = new Domain { Name = "contoso.example", FirstSeenUtc = DateTimeOffset.UtcNow, Groups = [group] };
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
        return (group.Id, domain.Id);
    }

    [Fact]
    public async Task SettingTheSystemTarget_StoresItAndAuditsOnce_FromNotSet()
    {
        await using (var context = CreateContext())
        {
            await DmarcTargetService.SetSystemAsync(context, TestActors.Admin, new DmarcTarget(DmarcPolicyLevel.Reject, DmarcPolicyLevel.Quarantine), CancellationToken.None);
        }

        await using var verify = CreateContext();
        var settings = await verify.DnsRecordSettings.SingleAsync();
        Assert.Equal(new DmarcTarget(DmarcPolicyLevel.Reject, DmarcPolicyLevel.Quarantine), settings.DmarcTarget);
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.DmarcTargetSaved, entry.Action);
        Assert.Contains(new AuditFieldChange("DMARC target", "not set", "p=reject; sp=quarantine"), entry.Changes);
    }

    [Fact]
    public async Task SettingAGroupTarget_StoresItAndAudits()
    {
        var (groupId, _) = await SeedGroupAndDomainAsync();

        bool found;
        await using (var context = CreateContext())
        {
            found = await DmarcTargetService.SetGroupAsync(context, TestActors.Admin, groupId, new DmarcTarget(DmarcPolicyLevel.Quarantine, null), CancellationToken.None);
        }

        Assert.True(found);
        await using var verify = CreateContext();
        Assert.Equal(new DmarcTarget(DmarcPolicyLevel.Quarantine, null), (await verify.Groups.SingleAsync()).DmarcTarget);
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Contains(new AuditFieldChange("DMARC target", "not set", "p=quarantine"), entry.Changes);
    }

    [Fact]
    public async Task SettingADomainTarget_StoresItAndAudits()
    {
        var (_, domainId) = await SeedGroupAndDomainAsync();

        bool found;
        await using (var context = CreateContext())
        {
            found = await DmarcTargetService.SetDomainAsync(context, TestActors.Admin, domainId, new DmarcTarget(DmarcPolicyLevel.None, null), CancellationToken.None);
        }

        Assert.True(found);
        await using var verify = CreateContext();
        Assert.Equal(new DmarcTarget(DmarcPolicyLevel.None, null), (await verify.Domains.SingleAsync()).DmarcTarget);
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Contains(new AuditFieldChange("DMARC target", "not set", "p=none"), entry.Changes);
    }

    [Fact]
    public async Task SavingTheSameValueAgain_RecordsNothing()
    {
        await using (var context = CreateContext())
        {
            await DmarcTargetService.SetSystemAsync(context, TestActors.Admin, new DmarcTarget(DmarcPolicyLevel.Reject, null), CancellationToken.None);
            await DmarcTargetService.SetSystemAsync(context, TestActors.Admin, new DmarcTarget(DmarcPolicyLevel.Reject, null), CancellationToken.None);
        }

        await using var verify = CreateContext();
        Assert.Equal(1, await verify.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task ClearingATarget_RecordsTheOldValueAndNotSet()
    {
        await using (var context = CreateContext())
        {
            await DmarcTargetService.SetSystemAsync(context, TestActors.Admin, new DmarcTarget(DmarcPolicyLevel.Reject, null), CancellationToken.None);
            await DmarcTargetService.SetSystemAsync(context, TestActors.Admin, null, CancellationToken.None);
        }

        await using var verify = CreateContext();
        Assert.Null((await verify.DnsRecordSettings.SingleAsync()).DmarcTarget);
        var latest = (await verify.AuditEntries.ToListAsync()).OrderBy(entry => entry.Id).Last();
        Assert.Contains(new AuditFieldChange("DMARC target", "p=reject", "not set"), latest.Changes);
    }

    [Fact]
    public async Task AnUnknownGroupOrDomain_ReturnsFalse_AndRecordsNothing()
    {
        bool groupFound;
        bool domainFound;
        await using (var context = CreateContext())
        {
            groupFound = await DmarcTargetService.SetGroupAsync(context, TestActors.Admin, 999, new DmarcTarget(DmarcPolicyLevel.Reject, null), CancellationToken.None);
            domainFound = await DmarcTargetService.SetDomainAsync(context, TestActors.Admin, 999, new DmarcTarget(DmarcPolicyLevel.Reject, null), CancellationToken.None);
        }

        Assert.False(groupFound);
        Assert.False(domainFound);
        await using var verify = CreateContext();
        Assert.Empty(await verify.AuditEntries.ToListAsync());
    }

    [Fact]
    public async Task ASubdomainPolicyEqualToThePolicy_IsStoredAsFollowingP()
    {
        await using (var context = CreateContext())
        {
            await DmarcTargetService.SetSystemAsync(context, TestActors.Admin, new DmarcTarget(DmarcPolicyLevel.Reject, DmarcPolicyLevel.Reject), CancellationToken.None);
        }

        await using var verify = CreateContext();
        var settings = await verify.DnsRecordSettings.SingleAsync();
        Assert.Equal(DmarcPolicyLevel.Reject, settings.TargetDmarcPolicy);
        Assert.Null(settings.TargetDmarcSubdomainPolicy);
    }

    [Fact]
    public async Task DeletingAGroupWithATarget_LeavesItsDomainsResolvingToTheSystemTarget()
    {
        var (groupId, domainId) = await SeedGroupAndDomainAsync();
        await using (var context = CreateContext())
        {
            await DmarcTargetService.SetSystemAsync(context, TestActors.Admin, new DmarcTarget(DmarcPolicyLevel.Quarantine, null), CancellationToken.None);
            await DmarcTargetService.SetGroupAsync(context, TestActors.Admin, groupId, new DmarcTarget(DmarcPolicyLevel.Reject, null), CancellationToken.None);
            context.Groups.Remove(await context.Groups.SingleAsync(group => group.Id == groupId));
            await context.SaveChangesAsync();
        }

        await using var verify = CreateContext();
        var domain = await verify.Domains.Include(candidate => candidate.Groups).SingleAsync(candidate => candidate.Id == domainId);
        var settings = await verify.DnsRecordSettings.SingleAsync();
        var effective = DmarcTargetResolver.Resolve(domain, settings);

        Assert.NotNull(effective);
        Assert.Equal(DmarcTargetSource.System, effective.Source);
        Assert.Equal(DmarcPolicyLevel.Quarantine, effective.Policy);
    }
}
