using DotMarc.Data;
using DotMarc.Psa;
using DotMarc.Psa.ConnectWise;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Psa.ConnectWise;

[Collection("Postgres")]
public sealed class ConnectWiseProviderTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ConnectWiseProviderTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    /// <summary>Answers GetTicketAsync with a fixed ticket (or none); nothing else is used by these tests.</summary>
    private sealed class FakeConnectWiseClient(ConnectWiseTicket? ticket) : IConnectWiseClient
    {
        public Task<ConnectWiseTicket?> GetTicketAsync(ConnectWiseSettings settings, string ticketId, CancellationToken cancellationToken = default) => Task.FromResult(ticket);
        public Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(ConnectWiseSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PsaOption>> ListBoardsAsync(ConnectWiseSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PsaOption>> ListBoardStatusesAsync(ConnectWiseSettings settings, int boardId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PsaOption>> ListBoardTypesAsync(ConnectWiseSettings settings, int boardId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PsaOption>> ListPrioritiesAsync(ConnectWiseSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CreateTicketAsync(ConnectWiseSettings settings, PsaTicketRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CloseTicketAsync(ConnectWiseSettings settings, string ticketId, string note, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private async Task SaveSettingsAsync(Action<ConnectWiseSettings> change)
    {
        await using var context = CreateContext();
        var settings = await context.ConnectWiseSettings.SingleAsync();
        change(settings);
        await context.SaveChangesAsync();
    }

    [Theory]
    [InlineData(true, 5, PsaTicketState.Closed)]    // the closed flag wins whatever the status
    [InlineData(false, 20, PsaTicketState.Closed)]  // the configured closed status
    [InlineData(false, 16, PsaTicketState.Open)]
    public async Task TicketState_UsesTheClosedFlagOrTheClosedStatus(bool closedFlag, int statusId, PsaTicketState expected)
    {
        await SaveSettingsAsync(settings => settings.ClosedStatusId = 20);
        await using var context = CreateContext();

        var state = await new ConnectWiseProvider(new FakeConnectWiseClient(new ConnectWiseTicket(closedFlag, statusId))).GetTicketStateAsync(context, "4321");

        Assert.Equal(expected, state);
    }

    [Fact]
    public async Task TicketState_AMissingTicket_IsMissing()
    {
        await using var context = CreateContext();

        Assert.Equal(PsaTicketState.Missing, await new ConnectWiseProvider(new FakeConnectWiseClient(null)).GetTicketStateAsync(context, "4321"));
    }

    [Fact]
    public async Task Readiness_ListsWhatIsMissing()
    {
        await SaveSettingsAsync(settings => settings.Enabled = true);
        await using var context = CreateContext();

        var readiness = await new ConnectWiseProvider(new FakeConnectWiseClient(null)).GetReadinessAsync(context);

        Assert.False(readiness.IsReady);
        Assert.Equal(["site URL", "company ID", "public key", "private key", "board", "new ticket status", "priority", "closed status"], readiness.Missing);
    }

    [Fact]
    public async Task TicketUrl_UsesTheWebHost()
    {
        await SaveSettingsAsync(settings => settings.SiteUrl = "api-eu.myconnectwise.net");
        await using var context = CreateContext();

        Assert.Equal("https://eu.myconnectwise.net/v4_6_release/ConnectWise.aspx?routeTo=ServiceFV&recid={0}",
            await new ConnectWiseProvider(new FakeConnectWiseClient(null)).GetTicketUrlTemplateAsync(context));
    }
}
