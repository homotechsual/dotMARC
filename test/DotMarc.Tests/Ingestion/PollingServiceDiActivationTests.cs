using DotMarc.Data;
using DotMarc.Graph;
using DotMarc.Ingestion;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DotMarc.Tests.Ingestion;

/// <summary>Regression test for a Critical bug found in review: PollingService originally had two
/// 3-parameter constructors, and the plan's assumption that DI would unambiguously pick the host
/// constructor was wrong — both IGraphMailboxClient (Task 5) and DotMarcDbContext (Task 2) are
/// also registered in the app's DI container, so both constructors had every parameter type
/// resolvable.
///
/// Two things had to be verified empirically here, since both were assumptions rather than facts:
/// 1. Plain `services.AddSingleton&lt;PollingService&gt;()` / `AddHostedService&lt;PollingService&gt;()`
///    (which activates via the container's own built-in constructor-selection logic) throws
///    `InvalidOperationException: ... ambiguous` even with `[ActivatorUtilitiesConstructor]` present
///    on the host constructor — the built-in container's own selection algorithm does NOT consult
///    that attribute; it is only honored by `ActivatorUtilities.CreateInstance`/`CreateFactory`.
///    Confirmed by temporarily removing the attribute and re-running this test: same exception
///    either way when going through a plain `AddSingleton&lt;PollingService&gt;()` registration.
/// 2. Routing activation through `ActivatorUtilities.CreateInstance&lt;PollingService&gt;(provider)`
///    explicitly (which Program.cs now does via
///    `AddHostedService&lt;PollingService&gt;(sp => ActivatorUtilities.CreateInstance&lt;PollingService&gt;(sp))`)
///    DOES honor `[ActivatorUtilitiesConstructor]` and deterministically selects the host
///    constructor — this is the actual fix; the attribute is necessary but registering via
///    `AddHostedService&lt;PollingService&gt;()` alone (as originally proposed) is not sufficient.
///
/// This test builds a ServiceCollection with the same registration shape as Program.cs (both
/// IGraphMailboxClient and DotMarcDbContext registered) and calls
/// `ActivatorUtilities.CreateInstance&lt;PollingService&gt;(provider)` the same way Program.cs's
/// `AddHostedService` factory does, confirming activation succeeds without ambiguity.</summary>
[Collection("Postgres")]
public sealed class PollingServiceDiActivationTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public PollingServiceDiActivationTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public void ActivatorUtilities_CanActivate_PollingService_WithoutAmbiguousConstructorError()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DotMarcDbContext>(options => options.UseNpgsql(_connectionString));
        services.AddSingleton<IGraphMailboxClient>(new NoOpGraphMailboxClient());
        services.AddSingleton<IAlertingService>(new NoOpAlertingService());
        services.Configure<GraphOptions>(o =>
        {
            o.ClientId = "test-client-id";
            o.TenantId = "test-tenant-id";
            o.ClientSecret = "test-client-secret";
            o.MailboxAddress = "dmarc@example.com";
        });

        using var provider = services.BuildServiceProvider();

        // This is the exact activation path Program.cs uses:
        // AddHostedService<PollingService>(sp => ActivatorUtilities.CreateInstance<PollingService>(sp)).
        // Before the [ActivatorUtilitiesConstructor] fix, this threw InvalidOperationException:
        // "Unable to activate type 'PollingService'. The following constructors are ambiguous ...".
        var exception = Record.Exception(() => ActivatorUtilities.CreateInstance<PollingService>(provider));

        Assert.Null(exception);
    }

    private sealed class NoOpGraphMailboxClient : IGraphMailboxClient
    {
        public Task<IReadOnlyList<MailboxMessage>> GetUnreadMessagesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MailboxMessage>>([]);

        public Task<IReadOnlyList<MailboxAttachment>> GetAttachmentsAsync(string messageId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MailboxAttachment>>([]);

        public Task MarkAsReadAsync(string messageId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpAlertingService : IAlertingService
    {
        public Task CheckPinnedDomainsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResolveDomainAlertAsync(string domainName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
