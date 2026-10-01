using DotMarc.Data;
using DotMarc.DomainImport;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.DomainImport;

[Collection("Postgres")]
public sealed class ImportSnapshotLoaderTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ImportSnapshotLoaderTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static ImportTable Table(string csv) => ImportTable.FromRows(CsvImportReader.Read(csv));

    [Fact]
    public async Task LoadsTheImportsDomains_WithTheirGroupsTagsAndSettings()
    {
        await using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com");
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "unrelated.com");
            await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client A", CancellationToken.None);
            await TagManagementService.AddTagAsync(context, TestActors.Admin, "primary", MudBlazor.Color.Primary);
            var domainId = (await context.Domains.SingleAsync(domain => domain.Name == "contoso.com")).Id;
            await GroupManagementService.SetDomainGroupsAsync(context, TestActors.Admin, domainId, [(await context.Groups.SingleAsync()).Id]);
            await DomainManagementService.SetDkimSelectorsAsync(context, TestActors.Admin, domainId, ["selector1"]);
        }

        await using var loadContext = CreateContext();
        var snapshot = await ImportSnapshotLoader.LoadAsync(loadContext, Table("Contoso.com\nnew.com"), null, "HaloPSA isn't connected.", new FakeMxHostsLookup(), CancellationToken.None);

        var contoso = Assert.Single(snapshot.DomainsByName).Value;
        Assert.Equal("contoso.com", contoso.Name);
        Assert.Equal(["Client A"], contoso.Groups);
        Assert.Equal(["selector1"], contoso.DkimSelectors);
        Assert.Equal(["Client A"], snapshot.GroupNames);
        Assert.Equal(["primary"], snapshot.TagNames);
        Assert.Null(snapshot.HaloClients);
        Assert.Equal("HaloPSA isn't connected.", snapshot.HaloUnavailableReason);
    }

    [Fact]
    public async Task ADomainStoredInUnicodeBeforeNamesWereNormalised_MatchesItsXnForm()
    {
        await using (var context = CreateContext())
        {
            context.Domains.Add(new Domain { Name = "bücher.example", FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = true });
            await context.SaveChangesAsync();
        }

        await using var loadContext = CreateContext();
        var snapshot = await ImportSnapshotLoader.LoadAsync(loadContext, Table("xn--bcher-kva.example"), null, null, new FakeMxHostsLookup(), CancellationToken.None);

        Assert.Equal("bücher.example", Assert.Single(snapshot.DomainsByName, pair => pair.Key == "xn--bcher-kva.example").Value.Name);
    }

    [Fact]
    public async Task LooksUpMxHosts_OnlyForDomainsTurningMtaStsOnWithoutAny()
    {
        var lookup = new FakeMxHostsLookup();
        lookup.HostsByDomain["needs.com"] = ["mail.needs.com"];

        await using var context = CreateContext();
        var snapshot = await ImportSnapshotLoader.LoadAsync(context,
            Table("domain,mta-sts mode,mx hosts\nneeds.com,testing,\ngiven.com,enforce,mail.given.com\noff.com,off,\nnothing.com,,"),
            null, null, lookup, CancellationToken.None);

        Assert.Equal(["needs.com"], lookup.LookedUp);
        Assert.Equal(["mail.needs.com"], snapshot.LookedUpMxHosts["needs.com"]);
    }
}
