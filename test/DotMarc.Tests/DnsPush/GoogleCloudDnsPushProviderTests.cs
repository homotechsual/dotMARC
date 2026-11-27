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

    private sealed record ForeignSession : IDnsPushSession;

    private static DnsRecordChange DkimCname(string zoneName) =>
        new(DnsRecordChangeKind.Create, "CNAME", $"selector1._domainkey.{zoneName}", $"selector1-{zoneName.Replace('.', '-')}._domainkey.example.onmicrosoft.com", null, zoneName);

    [Fact]
    public async Task OneExchange_ServesPushesToSeveralZones_WithOneTokenRequest()
    {
        var handler = new FakeHttpMessageHandler();
        handler.ResponseBodies.Enqueue("""{"access_token":"token"}""");
        handler.ResponseBodies.Enqueue("""{"projects":[{"projectId":"p1"}]}""");
        handler.ResponseBodies.Enqueue("""{"managedZones":[{"name":"z1","dnsName":"contoso.com."}]}""");
        handler.ResponseBodies.Enqueue("""{"rrsets":[]}""");
        handler.ResponseBodies.Enqueue("{}");
        handler.ResponseBodies.Enqueue("""{"projects":[{"projectId":"p1"}]}""");
        handler.ResponseBodies.Enqueue("""{"managedZones":[{"name":"z2","dnsName":"fabrikam.com."}]}""");
        handler.ResponseBodies.Enqueue("""{"rrsets":[]}""");
        handler.ResponseBodies.Enqueue("{}");
        var provider = await CreateProviderAsync(handler);

        var exchange = await provider.ExchangeAsync("code", "verifier", "https://dotmarc.example/callback", CancellationToken.None);
        var first = await provider.PushAsync(exchange.Session!, [DkimCname("contoso.com")], CancellationToken.None);
        var second = await provider.PushAsync(exchange.Session!, [DkimCname("fabrikam.com")], CancellationToken.None);

        Assert.Null(exchange.Failure);
        Assert.Equal(DnsPushOutcome.Pushed, first.Outcome);
        Assert.Equal(DnsPushOutcome.Pushed, second.Outcome);
        Assert.Single(handler.Requests, request => request.RequestUri!.Host == "oauth2.googleapis.com");
        Assert.Equal(9, handler.Requests.Count);
    }

    [Fact]
    public async Task APushToAZoneNoProjectHas_IsZoneNotFound_AndALaterPushToAnotherZoneStillWorks()
    {
        var handler = new FakeHttpMessageHandler();
        handler.ResponseBodies.Enqueue("""{"access_token":"token"}""");
        handler.ResponseBodies.Enqueue("""{"projects":[{"projectId":"p1"}]}""");
        handler.ResponseBodies.Enqueue("""{"managedZones":[]}""");
        handler.ResponseBodies.Enqueue("""{"projects":[{"projectId":"p1"}]}""");
        handler.ResponseBodies.Enqueue("""{"managedZones":[{"name":"z2","dnsName":"fabrikam.com."}]}""");
        handler.ResponseBodies.Enqueue("""{"rrsets":[]}""");
        handler.ResponseBodies.Enqueue("{}");
        var provider = await CreateProviderAsync(handler);
        var exchange = await provider.ExchangeAsync("code", "verifier", "https://dotmarc.example/callback", CancellationToken.None);

        var missing = await provider.PushAsync(exchange.Session!, [DkimCname("missing.example")], CancellationToken.None);
        var present = await provider.PushAsync(exchange.Session!, [DkimCname("fabrikam.com")], CancellationToken.None);

        Assert.Equal(DnsPushOutcome.ZoneNotFound, missing.Outcome);
        Assert.Equal(DnsPushOutcome.Pushed, present.Outcome);
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
    public async Task WhenGooglePushIsNotConfigured_ExchangeIsAFailure_AndNothingIsSent()
    {
        var handler = new FakeHttpMessageHandler();
        var provider = new GoogleCloudDnsPushProvider(new FakeDbContextFactory(_connectionString), new FakeSecretStore(), new HttpClient(handler));

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

        var result = await provider.PushAsync(new ForeignSession(), [DkimCname("contoso.com")], CancellationToken.None);

        Assert.Equal(DnsPushOutcome.ProviderError, result.Outcome);
        Assert.Empty(handler.Requests);
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

    [Fact]
    public async Task ReplaceTxtValues_SendsOtherValuesBackExactlyAsTheyWere()
    {
        var handler = new FakeHttpMessageHandler();
        handler.ResponseBodies.Enqueue("""{"access_token":"token"}""");
        handler.ResponseBodies.Enqueue("""{"projects":[{"projectId":"p1"}]}""");
        handler.ResponseBodies.Enqueue("""{"managedZones":[{"name":"z1","dnsName":"contoso.com."}]}""");
        handler.ResponseBodies.Enqueue("""{"rrsets":[{"name":"contoso.com.","type":"TXT","ttl":300,"rrdatas":["\"caf\\195\\169 verification\"","\"v=spf1 include:old.example ~all \""]}]}""");
        handler.ResponseBodies.Enqueue("{}");
        var provider = await CreateProviderAsync(handler);
        var change = new DnsRecordChange(DnsRecordChangeKind.ReplaceTxtValues, "TXT", "contoso.com", "v=spf1 include:new.example ~all",
            "v=spf1 include:old.example ~all ", "contoso.com", ValuesToRemove: ["v=spf1 include:old.example ~all "]);

        var result = await provider.ExchangeAndPushAsync("code", "verifier", "https://dotmarc.example/callback", [change], CancellationToken.None);

        Assert.Equal(DnsPushOutcome.Pushed, result.Outcome);
        using var body = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[4]);
        Assert.Equal(["\"caf\\195\\169 verification\"", "\"v=spf1 include:new.example ~all\""],
            body.RootElement.GetProperty("additions")[0].GetProperty("rrdatas").EnumerateArray().Select(rrdata => rrdata.GetString()));
    }

    [Fact]
    public async Task ACnameIsSentFullyQualified()
    {
        var handler = new FakeHttpMessageHandler();
        handler.ResponseBodies.Enqueue("""{"access_token":"token"}""");
        handler.ResponseBodies.Enqueue("""{"projects":[{"projectId":"p1"}]}""");
        handler.ResponseBodies.Enqueue("""{"managedZones":[{"name":"z1","dnsName":"contoso.com."}]}""");
        handler.ResponseBodies.Enqueue("""{"rrsets":[]}""");
        handler.ResponseBodies.Enqueue("{}");
        var provider = await CreateProviderAsync(handler);
        var change = new DnsRecordChange(DnsRecordChangeKind.Create, "CNAME", "selector1._domainkey.contoso.com", "selector1-contoso-com._domainkey.contoso.onmicrosoft.com", null, "contoso.com");

        await provider.ExchangeAndPushAsync("code", "verifier", "https://dotmarc.example/callback", [change], CancellationToken.None);

        using var body = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[4]);
        Assert.Equal("selector1-contoso-com._domainkey.contoso.onmicrosoft.com.", body.RootElement.GetProperty("additions")[0].GetProperty("rrdatas")[0].GetString());
    }
}
