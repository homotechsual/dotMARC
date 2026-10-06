using DotMarc.Data;
using DotMarc.Psa;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Psa;

[Collection("Postgres")]
public sealed class PsaDirectoryTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public PsaDirectoryTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task LoadsEachReadyPsa_ReportsAFailureByName_AndSkipsOnesNotReady()
    {
        await using var context = CreateContext();
        var halo = new FakePsaProvider(PsaKind.HaloPsa) { Companies = { new PsaCompany("7", "Contoso") } };
        var connectWise = new FakePsaProvider(PsaKind.ConnectWise) { FailWith = new HttpRequestException("refused") };
        var autotask = new FakePsaProvider(PsaKind.Autotask) { Ready = false };

        var lists = await new PsaDirectory([autotask, connectWise, halo], NullLogger<PsaDirectory>.Instance).LoadAsync(context);

        Assert.Equal([PsaKind.HaloPsa, PsaKind.ConnectWise], lists.Select(list => list.Psa));
        Assert.Equal("Contoso", Assert.Single(lists[0].Companies!).Name);
        Assert.Null(lists[1].Companies);
        Assert.Equal("ConnectWise companies couldn't be loaded: refused", lists[1].FailureReason);
    }

    [Fact]
    public async Task RefreshesTheStoredNamesOfLinkedCompanies()
    {
        await using (var setup = CreateContext())
        {
            setup.Groups.Add(new Group { Name = "Client A", PsaCompanyLinks = [new PsaCompanyLink { Psa = PsaKind.HaloPsa, CompanyId = "7", CompanyName = "7" }] });
            await setup.SaveChangesAsync();
        }

        await using var context = CreateContext();
        var halo = new FakePsaProvider(PsaKind.HaloPsa) { Companies = { new PsaCompany("7", "Contoso") } };

        await new PsaDirectory([halo], NullLogger<PsaDirectory>.Instance).LoadAsync(context);

        await using var verify = CreateContext();
        Assert.Equal("Contoso", (await verify.PsaCompanyLinks.SingleAsync()).CompanyName);
    }
}
