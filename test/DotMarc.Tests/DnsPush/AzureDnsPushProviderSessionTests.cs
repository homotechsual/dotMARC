using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.DnsPush;

/// <summary>Azure's token exchange goes through the real MSAL client, so only the parts that don't reach it can be tested here: the
/// not-configured case, and a session that isn't Azure's. The exchange-once, push-per-domain behaviour is the same code shape as
/// Cloudflare's and Google's and is tested there.</summary>
[Collection("Postgres")]
public sealed class AzureDnsPushProviderSessionTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AzureDnsPushProviderSessionTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private sealed record ForeignSession : IDnsPushSession;

    private AzureDnsPushProvider CreateProvider() => new(new FakeDbContextFactory(_connectionString), new FakeSecretStore());

    [Fact]
    public async Task WhenAzurePushIsNotConfigured_ExchangeIsAFailureWithNoSession()
    {
        var exchange = await CreateProvider().ExchangeAsync("code", "verifier", "https://dotmarc.example/callback", CancellationToken.None);

        Assert.Null(exchange.Session);
        Assert.Equal(DnsPushOutcome.ProviderError, exchange.Failure!.Outcome);
        Assert.Contains("not configured", exchange.Failure.DetailMessage);
    }

    [Fact]
    public async Task ASessionFromAnotherProvider_IsRefused()
    {
        var change = new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", "_dmarc.contoso.com", "v=DMARC1; p=none", null, "contoso.com");

        var result = await CreateProvider().PushAsync(new ForeignSession(), [change], CancellationToken.None);

        Assert.Equal(DnsPushOutcome.ProviderError, result.Outcome);
    }
}
