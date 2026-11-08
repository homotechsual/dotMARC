using DotMarc.Data;
using DotMarc.Psa;
using DotMarc.Psa.SuperOps;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Psa.SuperOps;

[Collection("Postgres")]
public sealed class SuperOpsProviderTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public SuperOpsProviderTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    private sealed class FakeSuperOpsClient(SuperOpsTicket? ticket) : ISuperOpsClient
    {
        public Task<SuperOpsTicket?> GetTicketAsync(SuperOpsSettings settings, string ticketId, CancellationToken cancellationToken = default) => Task.FromResult(ticket);
        public Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(SuperOpsSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SuperOpsPicklists> GetPicklistsAsync(SuperOpsSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CreateTicketAsync(SuperOpsSettings settings, PsaTicketRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CloseTicketAsync(SuperOpsSettings settings, string ticketId, string note, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private async Task SaveSettingsAsync(Action<SuperOpsSettings> change)
    {
        await using var context = CreateContext();
        var settings = await context.SuperOpsSettings.SingleAsync();
        change(settings);
        await context.SaveChangesAsync();
    }

    [Theory]
    [InlineData("Resolved", PsaTicketState.Closed)]   // SuperOps's own
    [InlineData("Closed", PsaTicketState.Closed)]
    [InlineData("closed", PsaTicketState.Closed)]     // the match ignores case
    [InlineData(" Resolved ", PsaTicketState.Closed)]
    [InlineData("Fixed In Review", PsaTicketState.Closed)] // the configured closed status
    [InlineData("Open", PsaTicketState.Open)]
    [InlineData("Waiting on Customer", PsaTicketState.Open)]
    [InlineData(null, PsaTicketState.Open)]
    public async Task TicketState_IsClosedForTheClosedStatusOrABuiltInDoneStatus(string? status, PsaTicketState expected)
    {
        await SaveSettingsAsync(settings => settings.ClosedStatus = "Fixed In Review");
        await using var context = CreateContext();

        var state = await new SuperOpsProvider(new FakeSuperOpsClient(new SuperOpsTicket(status))).GetTicketStateAsync(context, "4321");

        Assert.Equal(expected, state);
    }

    [Fact]
    public async Task TicketState_IsMissing_WhenSuperOpsHasNoSuchTicket()
    {
        await using var context = CreateContext();

        var state = await new SuperOpsProvider(new FakeSuperOpsClient(null)).GetTicketStateAsync(context, "4321");

        Assert.Equal(PsaTicketState.Missing, state);
    }

    [Fact]
    public async Task Readiness_OfTheSeededRow_IsNotReady_AndSaysWhatIsMissing()
    {
        await using var context = CreateContext();

        var readiness = await new SuperOpsProvider(new FakeSuperOpsClient(null)).GetReadinessAsync(context);

        Assert.False(readiness.IsReady);
        Assert.Equal(["subdomain", "API token"], readiness.Missing);
    }

    [Fact]
    public async Task Readiness_IsReady_OnceEnabledWithEverythingSet()
    {
        await SaveSettingsAsync(settings => (settings.Enabled, settings.Subdomain, settings.ApiTokenConfigured) = (true, "contoso", true));
        await using var context = CreateContext();

        var readiness = await new SuperOpsProvider(new FakeSuperOpsClient(null)).GetReadinessAsync(context);

        Assert.True(readiness.IsReady);
    }

    [Fact]
    public void MissingSettings_ListsEachBlankRequiredSetting()
    {
        var missing = SuperOpsProvider.MissingSettings(new SuperOpsSettings { RequestType = " ", NewStatus = null, ClosedStatus = "" });

        Assert.Equal(["subdomain", "API token", "request type", "new ticket status", "closed status"], missing);
    }

    [Fact]
    public async Task TicketLinks_AreNotOffered_BecauseTheWebAddressIsNotKnown()
    {
        await using var context = CreateContext();

        Assert.Null(await new SuperOpsProvider(new FakeSuperOpsClient(null)).GetTicketUrlTemplateAsync(context));
    }
}
