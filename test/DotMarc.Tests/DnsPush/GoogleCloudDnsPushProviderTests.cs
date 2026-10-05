using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.DnsPush;

[Collection("Postgres")]
public sealed class GoogleCloudDnsPushProviderTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public GoogleCloudDnsPushProviderTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private async Task<GoogleCloudDnsPushProvider> CreateProviderAsync(FakeHttpMessageHandler handler)
    {
        await using (var context = CreateContext())
        {
            (await context.GoogleCloudDnsSettings.SingleAsync()).ClientId = "client";
            await context.SaveChangesAsync();
        }

        var secrets = new FakeSecretStore();
        secrets.Secrets[GoogleCloudDnsSettings.SecretStoreKey] = "secret";
        return new GoogleCloudDnsPushProvider(new FakeDbContextFactory(_connectionString), secrets, new HttpClient(handler));
    }

    [Fact]
    public async Task ReplaceTxtValues_KeepsOtherValuesAtTheName()
    {
        var handler = new FakeHttpMessageHandler();
        handler.ResponseBodies.Enqueue("""{"access_token":"token"}""");
        handler.ResponseBodies.Enqueue("""{"projects":[{"projectId":"p1"}]}""");
        handler.ResponseBodies.Enqueue("""{"managedZones":[{"name":"z1","dnsName":"contoso.com."}]}""");
        handler.ResponseBodies.Enqueue("""{"rrsets":[{"name":"contoso.com.","type":"TXT","ttl":300,"rrdatas":["\"google-site-verification=abc\"","\"v=spf1 include:old.example ~all\""]}]}""");
        handler.ResponseBodies.Enqueue("{}");
        var provider = await CreateProviderAsync(handler);
        var change = new DnsRecordChange(DnsRecordChangeKind.ReplaceTxtValues, "TXT", "contoso.com", "v=spf1 include:new.example ~all",
            "v=spf1 include:old.example ~all", "contoso.com", ValuesToRemove: ["v=spf1 include:old.example ~all"]);

        var result = await provider.ExchangeAndPushAsync("code", "verifier", "https://dotmarc.example/callback", [change], CancellationToken.None);

        Assert.Equal(DnsPushOutcome.Pushed, result.Outcome);
        using var body = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[4]);
        var added = body.RootElement.GetProperty("additions")[0];
        Assert.Equal(300, added.GetProperty("ttl").GetInt32());
        Assert.Equal(["\"google-site-verification=abc\"", "\"v=spf1 include:new.example ~all\""],
            added.GetProperty("rrdatas").EnumerateArray().Select(rrdata => rrdata.GetString()));
        Assert.Equal(2, body.RootElement.GetProperty("deletions")[0].GetProperty("rrdatas").GetArrayLength());
    }
}
