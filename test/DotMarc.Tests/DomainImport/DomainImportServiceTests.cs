using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.DomainImport;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.DomainImport;

[Collection("Postgres")]
public sealed class DomainImportServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DomainImportServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    private async Task<ImportPlan> PlanAsync(string csv, ExistingDomainMode mode = ExistingDomainMode.Add)
    {
        var table = ImportTable.FromRows(CsvImportReader.Read(csv));
        await using var context = CreateContext();
        var snapshot = await ImportSnapshotLoader.LoadAsync(context, table, null, "HaloPSA isn't connected.", new FakeMxHostsLookup(), CancellationToken.None);
        return DomainImportPlanner.Plan(table, snapshot, mode, ImportPermissions.All);
    }

    private async Task<ImportResult> ApplyAsync(ImportPlan plan)
    {
        await using var context = CreateContext();
        return await DomainImportService.ApplyAsync(context, TestActors.Admin, plan);
    }

    private async Task<List<string>> GroupsOfAsync(string domainName)
    {
        await using var context = CreateContext();
        return await context.Domains.Where(domain => domain.Name == domainName).SelectMany(domain => domain.Groups.Select(group => group.Name)).OrderBy(name => name).ToListAsync();
    }

    private async Task SeedAsync(string domainName, params string[] groupNames)
    {
        await using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, TestActors.Admin, domainName);
        foreach (var groupName in groupNames)
        {
            await GroupManagementService.AddGroupAsync(context, TestActors.Admin, groupName, CancellationToken.None);
        }

        var domainId = (await context.Domains.SingleAsync(domain => domain.Name == domainName)).Id;
        var groupIds = await context.Groups.Where(group => groupNames.Contains(group.Name)).Select(group => group.Id).ToListAsync();
        await GroupManagementService.SetDomainGroupsAsync(context, TestActors.Admin, domainId, groupIds);
    }

    [Fact]
    public async Task Apply_AddsDomainsWithTheirGroupsTagsAndSettings_AndAuditsEachChange()
    {
        var plan = await PlanAsync("domain,groups,tags,monitored,dkim selectors\na.com,New Group,new-tag,no,s1\nb.com,,,,\nnot a domain,,,,");

        var result = await ApplyAsync(plan);

        Assert.Equal((2, 1), (result.Added, result.Invalid));
        Assert.Equal(["New Group"], await GroupsOfAsync("a.com"));
        await using var verify = CreateContext();
        var domainA = await verify.Domains.Include(domain => domain.Tags).SingleAsync(domain => domain.Name == "a.com");
        Assert.False(domainA.IsMonitored);
        Assert.Equal(["s1"], domainA.DkimSelectors);
        Assert.Equal("new-tag", domainA.Tags.Single().Name);
        var actions = await verify.AuditEntries.Select(entry => entry.Action).ToListAsync();
        Assert.Contains(AuditActions.GroupAdded, actions);
        Assert.Contains(AuditActions.TagAdded, actions);
        Assert.Equal(2, actions.Count(action => action == AuditActions.DomainAdded));
        Assert.Contains(AuditActions.DomainGroupsChanged, actions);
        var summary = await verify.AuditEntries.SingleAsync(entry => entry.Action == AuditActions.DomainsImported);
        Assert.Equal("Imported 2 domains, updated 0, skipped 1 invalid and 0 already monitored", summary.Summary);
    }

    [Fact]
    public async Task Apply_AddMode_KeepsExistingGroups_AndRemovesDashEntries()
    {
        await SeedAsync("old.com", "Client A", "Client B");
        var plan = await PlanAsync("domain,groups\nold.com,Client C;-Client B");

        var result = await ApplyAsync(plan);

        Assert.Equal(1, result.Updated);
        Assert.Equal(["Client A", "Client C"], await GroupsOfAsync("old.com"));
    }

    [Fact]
    public async Task Apply_MatchMode_ReplacesGroups()
    {
        await SeedAsync("old.com", "Client A", "Client B");
        var plan = await PlanAsync("domain,groups\nold.com,Client B", ExistingDomainMode.Match);

        await ApplyAsync(plan);

        Assert.Equal(["Client B"], await GroupsOfAsync("old.com"));
    }

    [Fact]
    public async Task Apply_SkipMode_LeavesExistingDomainsUntouched()
    {
        await SeedAsync("old.com", "Client A");
        var plan = await PlanAsync("domain,groups\nold.com,Client B", ExistingDomainMode.Skip);

        var result = await ApplyAsync(plan);

        Assert.Equal(1, result.SkippedExisting);
        Assert.Equal(["Client A"], await GroupsOfAsync("old.com"));
    }

    [Fact]
    public async Task Apply_UsesAGroupCreatedSinceThePreview_RatherThanADuplicate()
    {
        var plan = await PlanAsync("domain,groups\na.com,Late Group");
        await using (var context = CreateContext())
        {
            await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Late Group", CancellationToken.None);
        }

        await ApplyAsync(plan);

        await using var verify = CreateContext();
        Assert.Single(verify.Groups, group => group.Name == "Late Group");
        Assert.Equal(["Late Group"], await GroupsOfAsync("a.com"));
    }

    [Fact]
    public async Task Apply_SavesNothing_WhenItFailsPartway()
    {
        await SeedAsync("old.com");
        await using (var context = CreateContext())
        {
            await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Doomed", CancellationToken.None);
        }

        var plan = await PlanAsync("domain,groups\nnew.com,\nold.com,Doomed");
        await using (var context = CreateContext())
        {
            var doomedId = (await context.Groups.SingleAsync(group => group.Name == "Doomed")).Id;
            await GroupManagementService.RemoveGroupAsync(context, TestActors.Admin, doomedId);
        }

        await Assert.ThrowsAnyAsync<Exception>(() => ApplyAsync(plan));

        await using var verify = CreateContext();
        Assert.DoesNotContain(verify.Domains, domain => domain.Name == "new.com");
        Assert.DoesNotContain(verify.AuditEntries, entry => entry.Action == AuditActions.DomainsImported);
    }
}
