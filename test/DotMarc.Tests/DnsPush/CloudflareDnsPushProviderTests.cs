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

    private sealed record ForeignSession : IDnsPushSession;

    private static DnsRecordChange DmarcCreate(string zoneName) =>
        new(DnsRecordChangeKind.Create, "TXT", $"_dmarc.{zoneName}", "v=DMARC1; p=none", null, zoneName);

    [Fact]
    public async Task OneExchange_ServesPushesToSeveralZones_WithOneTokenRequest()
    {
        var handler = new FakeHttpMessageHandler();
        handler.ResponseBodies.Enqueue("""{"access_token":"token"}""");
        handler.ResponseBodies.Enqueue("""{"result":[{"id":"zone1"}]}""");
        handler.ResponseBodies.Enqueue("{}");
        handler.ResponseBodies.Enqueue("""{"result":[{"id":"zone2"}]}""");
        handler.ResponseBodies.Enqueue("{}");
        var provider = await CreateProviderAsync(handler);

        var exchange = await provider.ExchangeAsync("code", "verifier", "https://dotmarc.example/callback", CancellationToken.None);
        var first = await provider.PushAsync(exchange.Session!, [DmarcCreate("contoso.com")], CancellationToken.None);
        var second = await provider.PushAsync(exchange.Session!, [DmarcCreate("fabrikam.com")], CancellationToken.None);

        Assert.Null(exchange.Failure);
        Assert.Equal(DnsPushOutcome.Pushed, first.Outcome);
        Assert.Equal(DnsPushOutcome.Pushed, second.Outcome);
        Assert.Single(handler.Requests, request => request.RequestUri!.Host == "dash.cloudflare.com");
        Assert.Equal(5, handler.Requests.Count);
        Assert.EndsWith("/zones/zone1/dns_records", handler.Requests[2].RequestUri!.AbsolutePath);
        Assert.EndsWith("/zones/zone2/dns_records", handler.Requests[4].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task APushToAZoneTheAccountDoesntHave_IsZoneNotFound_AndALaterPushToAnotherZoneStillWorks()
    {
        var handler = new FakeHttpMessageHandler();
        handler.ResponseBodies.Enqueue("""{"access_token":"token"}""");
        handler.ResponseBodies.Enqueue("""{"result":[]}""");
        handler.ResponseBodies.Enqueue("""{"result":[{"id":"zone2"}]}""");
        handler.ResponseBodies.Enqueue("{}");
        var provider = await CreateProviderAsync(handler);
        var exchange = await provider.ExchangeAsync("code", "verifier", "https://dotmarc.example/callback", CancellationToken.None);

        var missing = await provider.PushAsync(exchange.Session!, [DmarcCreate("missing.example")], CancellationToken.None);
        var present = await provider.PushAsync(exchange.Session!, [DmarcCreate("fabrikam.com")], CancellationToken.None);

        Assert.Equal(DnsPushOutcome.ZoneNotFound, missing.Outcome);
        Assert.Equal(DnsPushOutcome.Pushed, present.Outcome);
    }

    [Fact]
    public async Task OnePush_StillStopsAtItsFirstFailingChange()
    {
        var handler = new FakeHttpMessageHandler();
        handler.ResponseBodies.Enqueue("""{"access_token":"token"}""");
        handler.ResponseBodies.Enqueue("""{"result":[]}""");
        var provider = await CreateProviderAsync(handler);
        var exchange = await provider.ExchangeAsync("code", "verifier", "https://dotmarc.example/callback", CancellationToken.None);

        var result = await provider.PushAsync(exchange.Session!, [DmarcCreate("missing.example"), DmarcCreate("fabrikam.com")], CancellationToken.None);

        Assert.Equal(DnsPushOutcome.ZoneNotFound, result.Outcome);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ARejectedAuthorizationCode_IsAFailureWithNoSession()
    {
        var handler = new FakeHttpMessageHandler();
        handler.StatusCodes.Enqueue(System.Net.HttpStatusCode.BadRequest);
        var provider = await CreateProviderAsync(handler);

        var exchange = await provider.ExchangeAsync("code", "verifier", "https://dotmarc.example/callback", CancellationToken.None);

        Assert.Null(exchange.Session);
        Assert.Equal(DnsPushOutcome.ProviderError, exchange.Failure!.Outcome);
        Assert.Contains("rejected the authorization code", exchange.Failure.DetailMessage);
    }

    [Fact]
    public async Task WhenCloudflarePushIsNotConfigured_ExchangeIsAFailure_AndNothingIsSent()
    {
        var handler = new FakeHttpMessageHandler();
        var provider = new CloudflareDnsPushProvider(new FakeDbContextFactory(_connectionString), new FakeSecretStore(), new HttpClient(handler));

        var exchange = await provider.ExchangeAsync("code", "verifier", "https://dotmarc.example/callback", CancellationToken.None);

        Assert.Null(exchange.Session);
        Assert.Contains("not configured", exchange.Failure!.DetailMessage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ASessionFromAnotherProvider_IsRefused_AndNothingIsSent()
    {
        var handler = new FakeHttpMessageHandler();
        var provider = await CreateProviderAsync(handler);

        var result = await provider.PushAsync(new ForeignSession(), [DmarcCreate("contoso.com")], CancellationToken.None);

        Assert.Equal(DnsPushOutcome.ProviderError, result.Outcome);
        Assert.Empty(handler.Requests);
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

    [Fact]
    public async Task ReplaceTxtValues_ReportsTheDuplicate_WhenTheOldRecordCantBeDeleted()
    {
        var handler = new FakeHttpMessageHandler();
        handler.ResponseBodies.Enqueue("""{"access_token":"token"}""");
        handler.ResponseBodies.Enqueue("""{"result":[{"id":"zone1"}]}""");
        handler.ResponseBodies.Enqueue("""{"result":[{"id":"r2","content":"\"v=spf1 include:old.example ~all \""}]}""");
        handler.ResponseBodies.Enqueue("{}");
        handler.ResponseBodies.Enqueue("{}");
        foreach (var status in new[] { System.Net.HttpStatusCode.OK, System.Net.HttpStatusCode.OK, System.Net.HttpStatusCode.OK, System.Net.HttpStatusCode.OK, System.Net.HttpStatusCode.InternalServerError })
        {
            handler.StatusCodes.Enqueue(status);
        }

        var provider = await CreateProviderAsync(handler);
        var change = new DnsRecordChange(DnsRecordChangeKind.ReplaceTxtValues, "TXT", "contoso.com", "v=spf1 include:new.example ~all",
            "v=spf1 include:old.example ~all ", "contoso.com", ValuesToRemove: ["v=spf1 include:old.example ~all "]);

        var result = await provider.ExchangeAndPushAsync("code", "verifier", "https://dotmarc.example/callback", [change], CancellationToken.None);

        Assert.Equal(DnsPushOutcome.LeftDuplicateRecord, result.Outcome);
        Assert.Equal(HttpMethod.Delete, handler.Requests[4].Method);
    }

    [Fact]
    public async Task ReplaceTxtValues_RefusesToAddASecondSpfRecord()
    {
        var handler = new FakeHttpMessageHandler();
        handler.ResponseBodies.Enqueue("""{"access_token":"token"}""");
        handler.ResponseBodies.Enqueue("""{"result":[{"id":"zone1"}]}""");
        handler.ResponseBodies.Enqueue("""{"result":[{"id":"r9","content":"\"v=spf1 include:added-in-the-console.example ~all\""}]}""");
        var provider = await CreateProviderAsync(handler);
        var change = new DnsRecordChange(DnsRecordChangeKind.ReplaceTxtValues, "TXT", "contoso.com", "v=spf1 include:new.example ~all", null, "contoso.com", ValuesToRemove: []);

        var result = await provider.ExchangeAndPushAsync("code", "verifier", "https://dotmarc.example/callback", [change], CancellationToken.None);

        Assert.Equal(DnsPushOutcome.ProviderError, result.Outcome);
        Assert.Equal(3, handler.Requests.Count);
    }
}
