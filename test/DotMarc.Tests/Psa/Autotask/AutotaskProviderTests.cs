using DotMarc.Data;
using DotMarc.Psa;
using DotMarc.Psa.Autotask;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Psa.Autotask;

[Collection("Postgres")]
public sealed class AutotaskProviderTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AutotaskProviderTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    /// <summary>Answers GetTicketStatusAsync with a fixed status (or none); nothing else is used by these tests.</summary>
    private sealed class FakeAutotaskClient(int? status) : IAutotaskClient
    {
        public Task<int?> GetTicketStatusAsync(AutotaskSettings settings, string ticketId, CancellationToken cancellationToken = default) => Task.FromResult(status);
        public Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(AutotaskSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AutotaskTicketPicklists> GetTicketPicklistsAsync(AutotaskSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CreateTicketAsync(AutotaskSettings settings, PsaTicketRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CloseTicketAsync(AutotaskSettings settings, string ticketId, string note, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private async Task SaveSettingsAsync(Action<AutotaskSettings> change)
    {
        await using var context = CreateContext();
        var settings = await context.AutotaskSettings.SingleAsync();
        change(settings);
        await context.SaveChangesAsync();
    }

    [Theory]
    [InlineData(5, PsaTicketState.Closed)]
    [InlineData(1, PsaTicketState.Open)]
    [InlineData(null, PsaTicketState.Missing)]
    public async Task TicketState_TheClosedStatusIsClosed_AndNoTicketIsMissing(int? status, PsaTicketState expected)
    {
        await using var context = CreateContext();

        Assert.Equal(expected, await new AutotaskProvider(new FakeAutotaskClient(status), new AutotaskZoneCache()).GetTicketStateAsync(context, "9001"));
    }

    [Fact]
    public async Task Readiness_ListsWhatIsMissing()
    {
        await SaveSettingsAsync(settings => settings.Enabled = true);
        await using var context = CreateContext();

        var readiness = await new AutotaskProvider(new FakeAutotaskClient(null), new AutotaskZoneCache()).GetReadinessAsync(context);

        Assert.False(readiness.IsReady);
        var expected = new List<string> { "username", "secret" };
        if (AutotaskSettings.DefaultIntegrationCode.Length == 0)
        {
            expected.Add("API tracking identifier");
        }

        expected.AddRange(["queue", "ticket type", "priority"]);
        Assert.Equal(expected, readiness.Missing);
    }

    [Fact]
    public async Task TicketUrl_IsKnownOnlyOnceTheZoneIs()
    {
        await SaveSettingsAsync(settings => settings.Username = "api@contoso.com");
        var zones = new AutotaskZoneCache();
        var provider = new AutotaskProvider(new FakeAutotaskClient(null), zones);
        await using var context = CreateContext();

        Assert.Null(await provider.GetTicketUrlTemplateAsync(context));

        zones.Set("api@contoso.com", new AutotaskZone("https://webservices2.autotask.net/ATServicesRest/V1.0/", "https://ww2.autotask.net/"));

        Assert.Equal("https://ww2.autotask.net/Autotask/AutotaskExtend/ExecuteCommand.aspx?Code=OpenTicketDetail&TicketID={0}",
            await provider.GetTicketUrlTemplateAsync(context));
    }
}
