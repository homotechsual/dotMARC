// test/DotMarc.Tests/Notifications/HaloPsaClientTests.cs
using System.Net;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class HaloPsaClientTests
{
    private sealed class FixedHaloSecretStore(string secret) : IHaloSecretStore
    {
        public Task SetClientSecretAsync(string clientSecret, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> GetClientSecretAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(secret);
    }

    private static HaloPsaSettings Settings => new()
    {
        AccountName = "contoso",
        AuthServerUrl = "https://contoso.halopsa.com/auth",
        ResourceServerUrl = "https://contoso.halopsa.com/api",
        ClientId = "client-id",
        TicketTypeId = 5,
        DefaultPriorityId = 2
    };

    private static (HaloPsaClient client, FakeHttpMessageHandler handler) CreateClient()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler);
        var client = new HaloPsaClient(http, new FixedHaloSecretStore("the-secret"), new HaloPsaTokenCache());
        return (client, handler);
    }

    [Fact]
    public async Task CreateTicketAsync_AcquiresATokenThenPostsTheTicket_AndReturnsTheNewTicketId()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600}""");
        handler.ResponseBodies.Enqueue("""{"id":4242}""");

        var ticketId = await client.CreateTicketAsync(Settings, haloClientId: 7, "contoso.io", "MissedReport", "Missing report", "contoso.io has not sent a report.");

        Assert.Equal("4242", ticketId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://contoso.halopsa.com/auth/token", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("Tickets", handler.Requests[1].RequestUri!.ToString());
        Assert.Equal("Bearer the-token", handler.Requests[1].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task CreateTicketAsync_ReusesTheCachedToken_WithinItsLifetime()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600}""");
        handler.ResponseBodies.Enqueue("""{"id":1}""");
        handler.ResponseBodies.Enqueue("""{"id":2}""");

        await client.CreateTicketAsync(Settings, 7, "a.example", "MissedReport", "t", "m");
        await client.CreateTicketAsync(Settings, 7, "b.example", "MissedReport", "t", "m");

        // One token request, two ticket-creation requests — the second call reused the cached token.
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(1, handler.Requests.Count(r => r.RequestUri!.ToString().EndsWith("/token")));
    }

    [Fact]
    public async Task CloseTicketAsync_PostsToTheTicketWithTheClosedStatus()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600}""");
        handler.ResponseBody = "{}";

        await client.CloseTicketAsync(Settings, "4242", "Resolved automatically by dotMARC.");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("Tickets", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task ListClientsAsync_ReturnsTheParsedClientList()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600}""");
        handler.ResponseBody = """{"clients":[{"id":1,"name":"Client A"},{"id":2,"name":"Client B"}]}""";

        var clients = await client.ListClientsAsync(Settings);

        Assert.Equal(2, clients.Count);
        Assert.Contains(clients, c => c is { Id: 1, Name: "Client A" });
    }
}
