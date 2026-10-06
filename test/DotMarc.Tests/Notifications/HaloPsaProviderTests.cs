using System.Net;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Psa;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class HaloPsaProviderTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public HaloPsaProviderTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    /// <summary>A Halo client whose tickets read as whatever status <paramref name="statusFor"/> says.</summary>
    private sealed class StatusHaloClient(Func<int, int> statusFor) : IHaloPsaClient
    {
        private readonly NoOpHaloPsaClient _noOp = new();

        public Task<IReadOnlyList<HaloClient>> ListClientsAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => _noOp.ListClientsAsync(settings, cancellationToken);
        public Task<IReadOnlyList<HaloTicketType>> ListTicketTypesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => _noOp.ListTicketTypesAsync(settings, cancellationToken);
        public Task<IReadOnlyList<HaloTicketStatus>> ListStatusesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => _noOp.ListStatusesAsync(settings, cancellationToken);
        public Task<IReadOnlyList<HaloPriority>> ListPrioritiesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => _noOp.ListPrioritiesAsync(settings, cancellationToken);
        public Task<IReadOnlyList<HaloAgent>> ListAgentsAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => _noOp.ListAgentsAsync(settings, cancellationToken);
        public Task<string> CreateTicketAsync(HaloPsaSettings settings, int haloClientId, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default) => _noOp.CreateTicketAsync(settings, haloClientId, domainName, alertType, title, message, cancellationToken);
        public Task CloseTicketAsync(HaloPsaSettings settings, string ticketId, string note, CancellationToken cancellationToken = default) => _noOp.CloseTicketAsync(settings, ticketId, note, cancellationToken);
        public Task<int> GetTicketStatusAsync(HaloPsaSettings settings, int ticketId, CancellationToken cancellationToken = default) => Task.FromResult(statusFor(ticketId));
    }

    private async Task SaveSettingsAsync(Action<HaloPsaSettings> change)
    {
        await using var context = CreateContext();
        var settings = await context.HaloPsaSettings.SingleAsync();
        change(settings);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Readiness_ListsWhatTicketingStillNeeds()
    {
        await SaveSettingsAsync(settings => { settings.Enabled = true; settings.AuthServerUrl = "https://halo.example/auth"; });
        await using var context = CreateContext();

        var readiness = await new HaloPsaProvider(new NoOpHaloPsaClient()).GetReadinessAsync(context);

        Assert.False(readiness.IsReady);
        Assert.Equal(["resource server URL", "client ID", "client secret", "ticket type", "default priority", "closed status"], readiness.Missing);
    }

    [Fact]
    public async Task Readiness_IsReady_WithEverySettingSaved_AndNoWebhookSecret()
    {
        await using (var setup = CreateContext())
        {
            await PsaTestSupport.MakeHaloReadyAsync(setup);
        }

        await using var context = CreateContext();

        Assert.True((await new HaloPsaProvider(new NoOpHaloPsaClient()).GetReadinessAsync(context)).IsReady);
    }

    [Theory]
    [InlineData(9, PsaTicketState.Closed)]
    [InlineData(2, PsaTicketState.Open)]
    public async Task TicketState_ComparesTheStatusWithTheClosedStatus(int statusId, PsaTicketState expected)
    {
        await SaveSettingsAsync(settings => settings.ClosedStatusId = 9);
        await using var context = CreateContext();

        var state = await new HaloPsaProvider(new StatusHaloClient(_ => statusId)).GetTicketStateAsync(context, "100");

        Assert.Equal(expected, state);
    }

    [Fact]
    public async Task TicketState_ATicketHaloNoLongerHas_IsMissing()
    {
        await SaveSettingsAsync(settings => settings.ClosedStatusId = 9);
        await using var context = CreateContext();
        var provider = new HaloPsaProvider(new StatusHaloClient(_ => throw new HttpRequestException("gone", null, HttpStatusCode.NotFound)));

        Assert.Equal(PsaTicketState.Missing, await provider.GetTicketStateAsync(context, "100"));
    }

    [Fact]
    public async Task TicketUrl_IsTheWebAddressBesideTheApi()
    {
        await SaveSettingsAsync(settings => settings.ResourceServerUrl = "https://contoso.halopsa.com/api/");
        await using var context = CreateContext();

        Assert.Equal("https://contoso.halopsa.com/ticket?id={0}", await new HaloPsaProvider(new NoOpHaloPsaClient()).GetTicketUrlTemplateAsync(context));
    }
}
