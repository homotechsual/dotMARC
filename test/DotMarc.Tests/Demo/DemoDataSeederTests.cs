using DotMarc.Data;
using DotMarc.Demo;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Demo;

[Collection("Postgres")]
public sealed class DemoDataSeederTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DemoDataSeederTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static DemoDataset SampleDataset() => DemoDataGenerator.Generate(new Random(1), new DateTimeOffset(2026, 8, 28, 6, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task ResetAsync_WritesAllDomainsGroupsAndReports()
    {
        using var context = CreateContext();

        await DemoDataSeeder.ResetAsync(context, SampleDataset(), CancellationToken.None);

        using var verify = CreateContext();
        Assert.Equal(7, await verify.Domains.CountAsync());
        Assert.Equal(4, await verify.Groups.CountAsync());
        Assert.True(await verify.Reports.CountAsync() > 0);
        Assert.True(await verify.ReportRecords.CountAsync() > 0);
    }

    [Fact]
    public async Task ResetAsync_SeedsAdminAndViewerRolesAndGrants()
    {
        using var context = CreateContext();

        await DemoDataSeeder.ResetAsync(context, SampleDataset(), CancellationToken.None);

        using var verify = CreateContext();
        var admin = await verify.Roles.SingleAsync(r => r.Name == "Admin");
        var viewer = await verify.Roles.SingleAsync(r => r.Name == "Viewer");

        var adminGrant = await verify.UserAccesses.SingleAsync(u => u.Email == DemoDataSeeder.AdminEmail);
        Assert.Equal(admin.Id, adminGrant.RoleId);

        var viewerGrant = await verify.UserAccesses
            .Include(u => u.ScopedGroups)
            .SingleAsync(u => u.Email == DemoDataSeeder.ViewerEmail);
        Assert.Equal(viewer.Id, viewerGrant.RoleId);
        Assert.Equal(DemoDataSeeder.ViewerScopedGroupName, Assert.Single(viewerGrant.ScopedGroups).Name);
    }

    [Fact]
    public async Task ResetAsync_IsRepeatable_WithoutAccumulatingDuplicateRows()
    {
        using (var context = CreateContext())
        {
            await DemoDataSeeder.ResetAsync(context, SampleDataset(), CancellationToken.None);
        }

        using (var context = CreateContext())
        {
            await DemoDataSeeder.ResetAsync(context, SampleDataset(), CancellationToken.None);
        }

        using var verify = CreateContext();
        Assert.Equal(7, await verify.Domains.CountAsync());
        Assert.Equal(2, await verify.Roles.CountAsync());
        Assert.Equal(2, await verify.UserAccesses.CountAsync());
    }

    [Fact]
    public async Task ResetAsync_WritesMtaStsFieldsOntoDomains()
    {
        using var context = CreateContext();

        await DemoDataSeeder.ResetAsync(context, SampleDataset(), CancellationToken.None);

        using var verify = CreateContext();
        var aurora = await verify.Domains.SingleAsync(d => d.Name == "aurora-retail.example");
        Assert.True(aurora.MtaStsEnabled);
        Assert.Equal(MtaStsStatus.Active, aurora.MtaStsStatus);
        Assert.Equal(MtaStsMode.Enforce, aurora.MtaStsMode);
        Assert.NotEmpty(aurora.MtaStsMxHosts);
        Assert.NotNull(aurora.MtaStsCheckedUtc);

        var driftwoodMedia = await verify.Domains.SingleAsync(d => d.Name == "driftwood-media.example");
        Assert.False(driftwoodMedia.MtaStsEnabled);
        Assert.Equal(MtaStsStatus.NotConfigured, driftwoodMedia.MtaStsStatus);
        Assert.Empty(driftwoodMedia.MtaStsMxHosts);
    }

    [Fact]
    public async Task ResetAsync_WritesPollCyclesAndParseFailures()
    {
        using var context = CreateContext();

        await DemoDataSeeder.ResetAsync(context, SampleDataset(), CancellationToken.None);

        using var verify = CreateContext();
        Assert.True(await verify.PollCycles.CountAsync() > 0);
        Assert.True(await verify.PollCycleDailySummaries.CountAsync() > 0);
        Assert.True(await verify.ParseFailures.CountAsync() > 0);
    }

    /// <summary>Proves ResetAsync's truncate-then-write is one atomic transaction, per the design
    /// spec's explicit requirement. Seeds a valid baseline, then feeds a dataset engineered to
    /// throw mid-WriteAsync (two domains sharing a name, violating Domain's unique index on
    /// Name) into a second ResetAsync call. If the truncate and write were not in the same
    /// transaction, the failed reset would still have committed the TRUNCATE, leaving the
    /// database empty (denying every demo visitor access until the next scheduled reset). With
    /// the fix, the whole attempt rolls back and the baseline data seeded before it is
    /// untouched.</summary>
    [Fact]
    public async Task ResetAsync_RollsBackTheTruncate_WhenWriteAsyncFailsPartway()
    {
        using (var context = CreateContext())
        {
            await DemoDataSeeder.ResetAsync(context, SampleDataset(), CancellationToken.None);
        }

        using (var verifyBaseline = CreateContext())
        {
            Assert.Equal(7, await verifyBaseline.Domains.CountAsync());
        }

        var validDataset = SampleDataset();
        var brokenDataset = validDataset with
        {
            Domains =
            [
                validDataset.Domains[0],
                validDataset.Domains[1] with { Name = validDataset.Domains[0].Name },
                .. validDataset.Domains.Skip(2)
            ]
        };

        using (var context = CreateContext())
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => DemoDataSeeder.ResetAsync(context, brokenDataset, CancellationToken.None));
        }

        using var verify = CreateContext();
        Assert.Equal(7, await verify.Domains.CountAsync());
        Assert.Equal(2, await verify.UserAccesses.CountAsync());
    }
}
