using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class DomainManagementServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DomainManagementServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private DotMarcDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DotMarcDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new DotMarcDbContext(options);
    }

    [Fact]
    public async Task AddDomainAsync_CreatesAPinnedDomain_WithNormalizedName()
    {
        using var context = CreateContext();

        var result = await DomainManagementService.AddDomainAsync(context, "Contoso.COM", CancellationToken.None);

        Assert.Equal(DomainManagementService.AddDomainResult.Added, result);
        var domain = context.Domains.Single();
        Assert.Equal("contoso.com", domain.Name);
        Assert.True(domain.IsPinned);
        Assert.Null(domain.LastReportReceivedUtc);
    }

    [Fact]
    public async Task AddDomainAsync_RejectsInvalidName()
    {
        using var context = CreateContext();

        var result = await DomainManagementService.AddDomainAsync(context, "not-a-domain", CancellationToken.None);

        Assert.Equal(DomainManagementService.AddDomainResult.InvalidName, result);
        Assert.Empty(context.Domains);
    }

    [Fact]
    public async Task AddDomainAsync_RejectsDuplicate_RegardlessOfCasing()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, "contoso.com", CancellationToken.None);

        var result = await DomainManagementService.AddDomainAsync(context, "CONTOSO.com", CancellationToken.None);

        Assert.Equal(DomainManagementService.AddDomainResult.AlreadyMonitored, result);
        Assert.Single(context.Domains);
    }
}
