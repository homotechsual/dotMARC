using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.DnsPush;

[Collection("Postgres")]
public sealed class CloudflareDnsPushProviderTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public CloudflareDnsPushProviderTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private async Task<CloudflareDnsPushProvider> CreateProviderAsync(FakeHttpMessageHandler handler)
    {
        await using (var context = CreateContext())
        {
            (await context.CloudflareDnsSettings.SingleAsync()).ClientId = "client";
            await context.SaveChangesAsync();
        }

        var secrets = new FakeSecretStore();
        secrets.Secrets[CloudflareDnsSettings.SecretStoreKey] = "secret";
        return new CloudflareDnsPushProvider(new FakeDbContextFactory(_connectionString), secrets, new HttpClient(handler));
    }

    [Fact]
    public async Task ReplaceTxtValues_KeepsOtherValuesAtTheName()
    {
        var handler = new FakeHttpMessageHandler();
        handler.ResponseBodies.Enqueue("""{"access_token":"token"}""");
        handler.ResponseBodies.Enqueue("""{"result":[{"id":"zone1"}]}""");
        handler.ResponseBodies.Enqueue("""{"result":[{"id":"r1","content":"\"google-site-verification=abc\""},{"id":"r2","content":"\"v=spf1 include:old.example ~all\""}]}""");
        handler.ResponseBodies.Enqueue("{}");
        handler.ResponseBodies.Enqueue("{}");
        var provider = await CreateProviderAsync(handler);
        var change = new DnsRecordChange(DnsRecordChangeKind.ReplaceTxtValues, "TXT", "contoso.com", "v=spf1 include:new.example ~all",
            "v=spf1 include:old.example ~all", "contoso.com", ValuesToRemove: ["v=spf1 include:old.example ~all"]);

        var result = await provider.ExchangeAndPushAsync("code", "verifier", "https://dotmarc.example/callback", [change], CancellationToken.None);

        Assert.Equal(DnsPushOutcome.Pushed, result.Outcome);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[3].Method);
        Assert.Contains("v=spf1 include:new.example ~all", handler.RequestBodies[3]);
        Assert.Equal(HttpMethod.Delete, handler.Requests[4].Method);
        Assert.EndsWith("/dns_records/r2", handler.Requests[4].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ReplaceTxtValues_ChangesNothing_WhenTheOldValueIsGone()
    {
        var handler = new FakeHttpMessageHandler();
        handler.ResponseBodies.Enqueue("""{"access_token":"token"}""");
        handler.ResponseBodies.Enqueue("""{"result":[{"id":"zone1"}]}""");
        handler.ResponseBodies.Enqueue("""{"result":[{"id":"r1","content":"\"v=spf1 -all\""}]}""");
        var provider = await CreateProviderAsync(handler);
        var change = new DnsRecordChange(DnsRecordChangeKind.ReplaceTxtValues, "TXT", "contoso.com", "v=spf1 ~all",
            "v=spf1 include:old.example ~all", "contoso.com", ValuesToRemove: ["v=spf1 include:old.example ~all"]);

        var result = await provider.ExchangeAndPushAsync("code", "verifier", "https://dotmarc.example/callback", [change], CancellationToken.None);

        Assert.Equal(DnsPushOutcome.ProviderError, result.Outcome);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ALongTxtValue_IsSentAsQuotedStrings()
    {
        var handler = new FakeHttpMessageHandler();
        handler.ResponseBodies.Enqueue("""{"access_token":"token"}""");
        handler.ResponseBodies.Enqueue("""{"result":[{"id":"zone1"}]}""");
        handler.ResponseBodies.Enqueue("{}");
        var provider = await CreateProviderAsync(handler);
        var key = "v=DKIM1; k=rsa; p=" + new string('A', 300);
        var change = new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", "google._domainkey.contoso.com", key, null, "contoso.com");

        await provider.ExchangeAndPushAsync("code", "verifier", "https://dotmarc.example/callback", [change], CancellationToken.None);

        Assert.Contains("\\u0022 \\u0022", handler.RequestBodies[2].Replace("\\\" \\\"", "\\u0022 \\u0022"));
    }
}
