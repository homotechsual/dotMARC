using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotMarc.Tests;

/// <summary>Regression test for a bug the re-review of this fix wave's DbContextFactory change
/// caught: registering BOTH AddDbContext&lt;DotMarcDbContext&gt; and
/// AddDbContextFactory&lt;DotMarcDbContext&gt; together (the original shape of that fix) creates a
/// scoped/singleton DbContextOptions&lt;DotMarcDbContext&gt; conflict that only surfaces when the
/// container validates scopes. WebApplication.CreateBuilder enables ValidateScopes/ValidateOnBuild
/// by default in the Development environment, but not in Production — so a plain `dotnet build`
/// and even a Docker smoke test (Production by default, since the Dockerfile sets no explicit
/// ASPNETCORE_ENVIRONMENT) both missed it; only actually starting the host with
/// ASPNETCORE_ENVIRONMENT=Development throws.
///
/// This test builds a ServiceProvider with ValidateScopes/ValidateOnBuild explicitly enabled
/// (mirroring what CreateBuilder does in Development) using the exact registration shape
/// Program.cs uses today — AddDbContextFactory only, no separate AddDbContext call — confirming:
/// 1. BuildServiceProvider itself doesn't throw (this is where the bug, if reintroduced, throws:
///    "Cannot consume scoped service 'DbContextOptions&lt;DotMarcDbContext&gt;' from singleton
///    'IDbContextFactory&lt;DotMarcDbContext&gt;'").
/// 2. IDbContextFactory&lt;DotMarcDbContext&gt; resolves (used by Dashboard.razor/DomainDetail.razor).
/// 3. DotMarcDbContext also resolves from a scope (used by PollingService's existing
///    IServiceScopeFactory-based resolution) — this is exactly what the fix must not break, since
///    AddDbContextFactory registers DotMarcDbContext itself as scoped too, without needing a
///    separate AddDbContext call.</summary>
[Collection("Postgres")]
public sealed class ProgramDiValidationTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ProgramDiValidationTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options;
        await using var context = new DotMarcDbContext(options);
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    [Fact]
    public void ServiceProvider_BuildsCleanly_WithScopeValidationEnabled_UsingOnlyAddDbContextFactory()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Exactly Program.cs's registration shape: AddDbContextFactory only, no AddDbContext
        // registered alongside it.
        services.AddDbContextFactory<DotMarcDbContext>(options => options.UseNpgsql(_connectionString));

        // ValidateScopes + ValidateOnBuild is what WebApplication.CreateBuilder turns on in the
        // Development environment. This call is the actual assertion: before the fix (AddDbContext
        // + AddDbContextFactory registered together), this line itself throws
        // InvalidOperationException.
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        // IDbContextFactory resolves directly (singleton) — used by the Blazor Server pages.
        var factory = provider.GetRequiredService<IDbContextFactory<DotMarcDbContext>>();
        Assert.NotNull(factory);

        // DotMarcDbContext also resolves from a scope — used by PollingService's existing
        // IServiceScopeFactory-based resolution, which must keep working unchanged.
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DotMarcDbContext>();
        Assert.NotNull(context);
    }
}
