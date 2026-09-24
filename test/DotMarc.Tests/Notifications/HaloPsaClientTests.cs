// test/DotMarc.Tests/Notifications/HaloPsaClientTests.cs
using System.Net;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class HaloPsaClientTests
{
    private sealed class FixedSecretStore(string secret) : ISecretStore
    {
        public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(secret);
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
        var client = new HaloPsaClient(http, new FixedSecretStore("the-secret"), new HaloPsaTokenCache());
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
    public async Task TheTokenRequest_AsksOnlyForScopesHaloRecognises()
    {
        // Halo rejects the whole token request with invalid_scope if any one scope is unknown,
        // and read:teams is not a Halo scope - so that combination must never be sent again.
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600}""");
        handler.ResponseBody = """[{"id":1,"name":"Low"}]""";

        await client.ListPrioritiesAsync(Settings);

        var tokenRequestBody = System.Net.WebUtility.UrlDecode(handler.RequestBodies[0]);
        Assert.Contains("grant_type=client_credentials", tokenRequestBody);
        Assert.Contains("scope=edit:tickets read:tickets read:customers", tokenRequestBody);
        Assert.DoesNotContain("read:teams", tokenRequestBody);
    }

    [Fact]
    public async Task ARejectedApiCall_ThrowsWithHalosExplanationInTheMessage()
    {
        // A bare "400 Bad Request" doesn't say which field Halo objected to, so the error carries the
        // start of Halo's own answer.
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600}""");
        handler.ResponseBodies.Enqueue("""{"message":"priority_id 99 is not valid"}""");
        handler.StatusCodes.Enqueue(HttpStatusCode.OK);
        handler.StatusCodes.Enqueue(HttpStatusCode.BadRequest);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CreateTicketAsync(Settings, haloClientId: 7, "contoso.io", "MissedReport", "t", "m"));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("400", exception.Message);
        Assert.Contains("Tickets", exception.Message);
        Assert.Contains("priority_id 99 is not valid", exception.Message);
    }

    [Fact]
    public async Task ARejectedApiCall_WithAnEmptyBody_StillReportsTheStatus()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600}""");
        handler.ResponseBodies.Enqueue("");
        handler.StatusCodes.Enqueue(HttpStatusCode.OK);
        handler.StatusCodes.Enqueue(HttpStatusCode.Forbidden);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CreateTicketAsync(Settings, 7, "contoso.io", "MissedReport", "t", "m"));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Contains("403", exception.Message);
        Assert.DoesNotContain("Halo said", exception.Message);
    }

    [Fact]
    public async Task AForbiddenApiCall_SaysWhichScopeHaloGrantedTheToken()
    {
        // Halo answers a permission problem with an empty 403, so the granted scope is the only clue.
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600,"scope":"edit:tickets read:tickets"}""");
        handler.ResponseBodies.Enqueue("");
        handler.StatusCodes.Enqueue(HttpStatusCode.OK);
        handler.StatusCodes.Enqueue(HttpStatusCode.Forbidden);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.ListClientsAsync(Settings));

        Assert.Contains("403", exception.Message);
        Assert.Contains("Scope granted to the token: edit:tickets read:tickets.", exception.Message);
    }

    [Fact]
    public async Task AForbiddenApiCall_WhenHaloReportsNoScope_SaysSo()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600}""");
        handler.ResponseBodies.Enqueue("");
        handler.StatusCodes.Enqueue(HttpStatusCode.OK);
        handler.StatusCodes.Enqueue(HttpStatusCode.Forbidden);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.ListClientsAsync(Settings));

        Assert.Contains("Scope granted to the token: not reported by Halo.", exception.Message);
    }

    [Fact]
    public async Task ARejectedTokenRequest_ThrowsWithHalosOAuthErrorInTheMessage()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"error":"invalid_scope","error_description":"The specified 'scope' parameter is not valid."}""");
        handler.StatusCodes.Enqueue(HttpStatusCode.BadRequest);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.ListPrioritiesAsync(Settings));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("invalid_scope", exception.Message);
        Assert.Contains("The specified 'scope' parameter is not valid.", exception.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ARejectedTokenRequest_WithNoOAuthBody_StillReportsTheStatusCode()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("not json");
        handler.StatusCodes.Enqueue(HttpStatusCode.BadGateway);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.ListPrioritiesAsync(Settings));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Contains("502", exception.Message);
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

        // One token request, two ticket-creation requests - the second call reused the cached token.
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(1, handler.Requests.Count(r => r.RequestUri!.ToString().EndsWith("/token")));
    }

    [Fact]
    public async Task CloseTicketAsync_PostsAnArrayToTicketsWithTheIdAndClosedStatus()
    {
        // Halo updates a ticket by posting an array holding the ticket's id and changed fields to Tickets.
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600}""");
        handler.ResponseBody = "{}";
        var settings = Settings;
        settings.ClosedStatusId = 9;

        await client.CloseTicketAsync(settings, "4242", "Resolved automatically by dotMARC.");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://contoso.halopsa.com/api/Tickets", handler.Requests[1].RequestUri!.ToString());
        using var sent = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[1]);
        var update = Assert.Single(sent.RootElement.EnumerateArray());
        Assert.Equal(4242, update.GetProperty("id").GetInt32());
        Assert.Equal(9, update.GetProperty("status_id").GetInt32());
    }

    [Fact]
    public async Task CreateTicketAsync_PostsAnArrayHoldingOneTicket()
    {
        // Halo refuses a bare object with "requires a JSON array".
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600}""");
        handler.ResponseBodies.Enqueue("""{"id":4242}""");

        await client.CreateTicketAsync(Settings, haloClientId: 7, "contoso.io", "MissedReport", "Missing report", "m");

        using var sent = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[1]);
        var ticket = Assert.Single(sent.RootElement.EnumerateArray());
        Assert.Equal("Missing report", ticket.GetProperty("summary").GetString());
        Assert.Equal(7, ticket.GetProperty("client_id").GetInt32());
        Assert.Equal(5, ticket.GetProperty("tickettype_id").GetInt32());
        Assert.Equal(2, ticket.GetProperty("priority_id").GetInt32());
    }

    [Fact]
    public async Task CreateTicketAsync_AcceptsTheCreatedTicketWrappedInAnArray()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600}""");
        handler.ResponseBodies.Enqueue("""[{"id":4243,"summary":"x"}]""");

        var ticketId = await client.CreateTicketAsync(Settings, 7, "contoso.io", "MissedReport", "t", "m");

        Assert.Equal("4243", ticketId);
    }

    [Fact]
    public async Task CreateTicketAsync_WhenTheResponseHasNoId_SaysWhatHaloSent()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600}""");
        handler.ResponseBodies.Enqueue("""{"summary":"x"}""");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.CreateTicketAsync(Settings, 7, "contoso.io", "MissedReport", "t", "m"));

        Assert.Contains("didn't include the ticket's id", exception.Message);
        Assert.Contains("\"summary\"", exception.Message);
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

    [Fact]
    public async Task ListPrioritiesAsync_ReturnsTheParsedPriorityList()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600}""");
        handler.ResponseBody = """[{"id":1,"name":"Low"},{"id":2,"name":"High"}]""";

        var priorities = await client.ListPrioritiesAsync(Settings);

        Assert.Equal(2, priorities.Count);
        Assert.Contains(priorities, p => p is { Id: 1, Name: "Low" });
        Assert.Contains(priorities, p => p is { Id: 2, Name: "High" });
        Assert.Contains("Priority", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task ListPrioritiesAsync_AcceptsIdsWrittenAsNumbersStringsOrDecimals()
    {
        // GET /api/Priority returns its ids as strings, and Halo writes some ids as 3.0 elsewhere.
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600}""");
        handler.ResponseBody = """[{"id":"1","name":"Low"},{"id":"2.0","name":"Medium"},{"id":3.0,"name":"High"},{"id":4,"name":"Critical"}]""";

        var priorities = await client.ListPrioritiesAsync(Settings);

        Assert.Equal([1, 2, 3, 4], priorities.Select(p => p.Id));
        Assert.Equal(["Low", "Medium", "High", "Critical"], priorities.Select(p => p.Name));
    }

    [Fact]
    public async Task ListPrioritiesAsync_UsesPriorityIdRatherThanTheRowGuid_AndCollapsesTheSlaRows()
    {
        // The real shape of GET /api/Priority: one row per priority per SLA. Each row's "id" is a
        // GUID; the number a ticket's priority_id takes is "priorityid". Hidden rows aren't selectable.
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600}""");
        handler.ResponseBody = """
            [
              {"id":"c183eb27-0a8a-4380-59fa-08d5c0811dad","slaid":2,"priorityid":1,"name":"Urgent","fixtime":48.0,"ishidden":false,"colour":"#f44e3b"},
              {"id":"0d2f7a10-1111-4222-8333-444455556666","slaid":3,"priorityid":1,"name":"Urgent","fixtime":24.0,"ishidden":false},
              {"id":"9a9a9a9a-1111-4222-8333-444455556666","slaid":2,"priorityid":2,"name":"High","ishidden":false},
              {"id":"1b1b1b1b-1111-4222-8333-444455556666","slaid":3,"priorityid":2,"name":"High","ishidden":false},
              {"id":"7c7c7c7c-1111-4222-8333-444455556666","slaid":2,"priorityid":9,"name":"Retired","ishidden":true}
            ]
            """;

        var priorities = await client.ListPrioritiesAsync(Settings);

        Assert.Equal([1, 2], priorities.Select(p => p.Id));
        Assert.Equal(["Urgent", "High"], priorities.Select(p => p.Name));
    }

    [Theory]
    [InlineData("""[{"id":"abc-123","name":"Low"}]""")]
    [InlineData("""[{"id":"3.5","name":"Low"}]""")]
    [InlineData("""[{"id":"","name":"Low"}]""")]
    [InlineData("""[{"id":null,"name":"Low"}]""")]
    [InlineData("""[{"name":"Low"}]""")]
    public async Task ListPrioritiesAsync_WithNoUsablePriorityNumber_ThrowsShowingWhatHaloSent(string body)
    {
        // Refusing (rather than guessing) matters: a wrong id would later be sent back to Halo as a
        // ticket's priority. The message quotes the start of the response so the shape is visible.
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600}""");
        handler.ResponseBody = body;

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.ListPrioritiesAsync(Settings));

        Assert.Contains("Priority", exception.Message);
        Assert.Contains("neither a priorityid nor a numeric id", exception.Message);
        Assert.Contains("It began:", exception.Message);
        Assert.Contains(body[..10], exception.Message);
    }

    [Fact]
    public async Task ListPrioritiesAsync_WithAnUnexpectedShape_ThrowsWithATruncatedSample()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600}""");
        handler.ResponseBody = "{\"unexpected\":\"" + new string('x', 1000) + "\"}";

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.ListPrioritiesAsync(Settings));

        Assert.Contains("It began: {\"unexpected\":\"xxx", exception.Message);
        Assert.True(exception.Message.Length < 700, "the sample of the response should be truncated");
    }

    [Fact]
    public async Task DifferentSettings_WithDifferentClientIds_EachAcquireTheirOwnToken()
    {
        // A shared token cache, as HaloPsaClient normally gets via DI - this is what proves a
        // credential change (a different ClientId, here standing in for "the admin edited Alert
        // settings") isn't served the other credential's cached token.
        var handler = new FakeHttpMessageHandler();
        var sharedCache = new HaloPsaTokenCache();
        var client = new HaloPsaClient(new HttpClient(handler), new FixedSecretStore("the-secret"), sharedCache);

        var settingsA = new HaloPsaSettings { AccountName = "contoso", AuthServerUrl = "https://contoso.halopsa.com/auth", ResourceServerUrl = "https://contoso.halopsa.com/api", ClientId = "client-a", TicketTypeId = 5, DefaultPriorityId = 2 };
        var settingsB = new HaloPsaSettings { AccountName = "contoso", AuthServerUrl = "https://contoso.halopsa.com/auth", ResourceServerUrl = "https://contoso.halopsa.com/api", ClientId = "client-b", TicketTypeId = 5, DefaultPriorityId = 2 };

        handler.ResponseBodies.Enqueue("""{"access_token":"token-a","expires_in":3600}""");
        handler.ResponseBodies.Enqueue("""{"id":1}""");
        handler.ResponseBodies.Enqueue("""{"access_token":"token-b","expires_in":3600}""");
        handler.ResponseBodies.Enqueue("""{"id":2}""");

        await client.CreateTicketAsync(settingsA, 7, "a.example", "MissedReport", "t", "m");
        await client.CreateTicketAsync(settingsB, 7, "b.example", "MissedReport", "t", "m");

        Assert.Equal(2, handler.Requests.Count(r => r.RequestUri!.ToString().EndsWith("/token")));
        Assert.Equal("Bearer token-a", handler.Requests[1].Headers.Authorization!.ToString());
        Assert.Equal("Bearer token-b", handler.Requests[3].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task A401Response_InvalidatesTheCachedTokenAndRetriesExactlyOnce_ThenSucceeds()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"stale-token","expires_in":3600}"""); // initial token
        handler.ResponseBodies.Enqueue("{}"); // rejected ticket call, body unused
        handler.ResponseBodies.Enqueue("""{"access_token":"fresh-token","expires_in":3600}"""); // refreshed token
        handler.ResponseBodies.Enqueue("{}"); // retried close call succeeds
        handler.StatusCodes.Enqueue(HttpStatusCode.OK); // token
        handler.StatusCodes.Enqueue(HttpStatusCode.Unauthorized); // ticket call rejected
        handler.StatusCodes.Enqueue(HttpStatusCode.OK); // token refresh
        handler.StatusCodes.Enqueue(HttpStatusCode.OK); // ticket call retried

        await client.CloseTicketAsync(Settings, "4242", "Resolved automatically by dotMARC.");

        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(2, handler.Requests.Count(r => r.RequestUri!.ToString().EndsWith("/token")));
        Assert.Equal("Bearer stale-token", handler.Requests[1].Headers.Authorization!.ToString());
        Assert.Equal("Bearer fresh-token", handler.Requests[3].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task APersistent401Response_RetriesOnlyOnce_ThenThrows_InsteadOfLoopingForever()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"token-1","expires_in":3600}""");
        handler.ResponseBodies.Enqueue("{}");
        handler.ResponseBodies.Enqueue("""{"access_token":"token-2","expires_in":3600}""");
        handler.ResponseBodies.Enqueue("{}");
        handler.StatusCodes.Enqueue(HttpStatusCode.OK);
        handler.StatusCodes.Enqueue(HttpStatusCode.Unauthorized);
        handler.StatusCodes.Enqueue(HttpStatusCode.OK);
        handler.StatusCodes.Enqueue(HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CloseTicketAsync(Settings, "4242", "Resolved automatically by dotMARC."));

        // Exactly one retry: two token acquisitions, two ticket calls - never a third attempt.
        Assert.Equal(4, handler.Requests.Count);
    }
}
