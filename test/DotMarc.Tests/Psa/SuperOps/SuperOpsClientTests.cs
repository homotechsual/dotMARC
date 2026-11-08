using System.Net;
using System.Text.Json;
using DotMarc.Psa;
using DotMarc.Psa.SuperOps;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Psa.SuperOps;

public sealed class SuperOpsClientTests
{
    private static SuperOpsSettings Settings(Action<SuperOpsSettings>? change = null)
    {
        var settings = new SuperOpsSettings
        {
            Subdomain = "contoso", ApiTokenConfigured = true, RequestType = "Incident", NewStatus = "Open", ClosedStatus = "Resolved",
        };
        change?.Invoke(settings);
        return settings;
    }

    private static (SuperOpsClient Client, FakeHttpMessageHandler Handler) Create()
    {
        var handler = new FakeHttpMessageHandler();
        var secrets = new FakeSecretStore { Secrets = { [SuperOpsSettings.ApiTokenSecretKey] = "token-abc" } };
        return (new SuperOpsClient(new HttpClient(handler), secrets), handler);
    }

    private static JsonElement Variables(string requestBody) => JsonDocument.Parse(requestBody).RootElement.GetProperty("variables").GetProperty("input");

    [Fact]
    public async Task EveryRequest_IsAPostToTheRegionsEndpoint_SignedInWithTheTokenAndSubdomain()
    {
        var (client, handler) = Create();
        handler.ResponseBody = """{"data":{"getClientList":{"clients":[],"listInfo":{"hasMore":false}}}}""";

        await client.ListCompaniesAsync(Settings());

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.superops.ai/msp", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("token-abc", request.Headers.Authorization.Parameter);
        Assert.Equal("contoso", Assert.Single(request.Headers.GetValues("CustomerSubDomain")));
    }

    [Fact]
    public async Task TheEuRegion_UsesTheEuEndpoint()
    {
        var (client, handler) = Create();
        handler.ResponseBody = """{"data":{"getClientList":{"clients":[],"listInfo":{"hasMore":false}}}}""";

        await client.ListCompaniesAsync(Settings(settings => settings.Region = SuperOpsRegions.Eu));

        Assert.Equal("https://euapi.superops.ai/msp", Assert.Single(handler.Requests).RequestUri!.ToString());
    }

    [Fact]
    public async Task WithNoTokenSaved_ItRefusesBeforeSendingAnything()
    {
        var handler = new FakeHttpMessageHandler();
        var client = new SuperOpsClient(new HttpClient(handler), new FakeSecretStore());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListCompaniesAsync(Settings()));

        Assert.Contains("API token", exception.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Companies_ArePagedWhileThereIsMore_AndSortedByName()
    {
        var (client, handler) = Create();
        handler.ResponseBodies.Enqueue("""{"data":{"getClientList":{"clients":[{"accountId":"9001","name":"Zebra Ltd"}],"listInfo":{"hasMore":true}}}}""");
        handler.ResponseBodies.Enqueue("""{"data":{"getClientList":{"clients":[{"accountId":9002,"name":"Aardvark Ltd"}],"listInfo":{"hasMore":false}}}}""");

        var companies = await client.ListCompaniesAsync(Settings());

        Assert.Equal([new PsaCompany("9002", "Aardvark Ltd"), new PsaCompany("9001", "Zebra Ltd")], companies);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, Variables(handler.RequestBodies[1]).GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task CreateTicket_SendsTheSettingsAndTheAlert_AndReturnsTheId()
    {
        var (client, handler) = Create();
        handler.ResponseBody = """{"data":{"createTicket":{"ticketId":"123456789012345"}}}""";
        var request = new PsaTicketRequest("9001", "contoso.com", "DMARC failing", "DMARC failing for contoso.com", "Pass rate fell.\nSee <report>.");

        var id = await client.CreateTicketAsync(Settings(settings => (settings.Priority, settings.Category) = ("High", "Email")), request);

        Assert.Equal("123456789012345", id);
        var input = Variables(Assert.Single(handler.RequestBodies));
        Assert.Equal("DMARC failing for contoso.com", input.GetProperty("subject").GetString());
        Assert.Equal("9001", input.GetProperty("client").GetProperty("accountId").GetString());
        Assert.Equal("Open", input.GetProperty("status").GetString());
        Assert.Equal("Incident", input.GetProperty("requestType").GetString());
        Assert.Equal("INTEGRATION", input.GetProperty("source").GetString());
        Assert.Equal("High", input.GetProperty("priority").GetString());
        Assert.Equal("Email", input.GetProperty("category").GetString());
        Assert.False(input.TryGetProperty("urgency", out _), "An empty optional setting must not be sent.");

        // The description is HTML: markup in the alert is escaped and line breaks become tags.
        var description = input.GetProperty("description").GetString()!;
        Assert.Contains("Pass rate fell.<br/>See &lt;report&gt;.", description);
        Assert.Contains("Domain: contoso.com", description);
    }

    [Fact]
    public async Task CreateTicket_WhenSuperOpsReturnsOnlyErrors_ThrowsWithItsMessage()
    {
        var (client, handler) = Create();
        handler.ResponseBody = """{"data":{"createTicket":null},"errors":[{"message":"Invalid requestType: Nonsense"}]}""";

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.CreateTicketAsync(Settings(), new PsaTicketRequest("9001", "contoso.com", "x", "t", "m")));

        Assert.Contains("Invalid requestType: Nonsense", exception.Message);
    }

    [Fact]
    public async Task ErrorsBesideGoodData_AreIgnored()
    {
        var (client, handler) = Create();
        handler.ResponseBody = """{"data":{"getTicketList":{"tickets":[{"ticketId":"77","status":"Open"}]}},"errors":[{"message":"Cannot return null for non-nullable type"}]}""";

        var ticket = await client.GetTicketAsync(Settings(), "77");

        Assert.Equal("Open", ticket!.Status);
    }

    [Fact]
    public async Task GetTicket_FiltersByTheTicketId()
    {
        var (client, handler) = Create();
        handler.ResponseBody = """{"data":{"getTicketList":{"tickets":[{"ticketId":"77","status":"Resolved"}]}}}""";

        var ticket = await client.GetTicketAsync(Settings(), "77");

        Assert.Equal("Resolved", ticket!.Status);
        var condition = Variables(Assert.Single(handler.RequestBodies)).GetProperty("condition");
        Assert.Equal("ticketId", condition.GetProperty("attribute").GetString());
        Assert.Equal("is", condition.GetProperty("operator").GetString());
        Assert.Equal("77", condition.GetProperty("value").GetString());
    }

    [Fact]
    public async Task GetTicket_ReadsAStatusThatIsAnObjectWithAName()
    {
        var (client, handler) = Create();
        handler.ResponseBody = """{"data":{"getTicketList":{"tickets":[{"ticketId":77,"status":{"statusId":"5","name":"Closed"}}]}}}""";

        Assert.Equal("Closed", (await client.GetTicketAsync(Settings(), "77"))!.Status);
    }

    [Fact]
    public async Task GetTicket_ReturnsNull_WhenNoTicketMatches()
    {
        var (client, handler) = Create();
        handler.ResponseBody = """{"data":{"getTicketList":{"tickets":[]}}}""";

        Assert.Null(await client.GetTicketAsync(Settings(), "77"));
    }

    [Fact]
    public async Task GetTicket_NeverReturnsADifferentTicket()
    {
        var (client, handler) = Create();
        handler.ResponseBody = """{"data":{"getTicketList":{"tickets":[{"ticketId":"78","status":"Open"}]}}}""";

        Assert.Null(await client.GetTicketAsync(Settings(), "77"));
    }

    [Fact]
    public async Task CloseTicket_AddsAPrivateNoteFirst_ThenMovesTheTicketToTheClosedStatus()
    {
        var (client, handler) = Create();
        handler.ResponseBodies.Enqueue("""{"data":{"createTicketNote":{"noteId":"1"}}}""");
        handler.ResponseBodies.Enqueue("""{"data":{"updateTicket":{"ticketId":"77"}}}""");

        await client.CloseTicketAsync(Settings(), "77", "Alert resolved.\nAll clear.");

        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Contains("createTicketNote", handler.RequestBodies[0]);
        var note = Variables(handler.RequestBodies[0]);
        Assert.Equal("77", note.GetProperty("ticket").GetProperty("ticketId").GetString());
        Assert.Equal("PRIVATE", note.GetProperty("privacyType").GetString());
        Assert.Equal("Alert resolved.<br/>All clear.", note.GetProperty("content").GetString());

        Assert.Contains("updateTicket", handler.RequestBodies[1]);
        var update = Variables(handler.RequestBodies[1]);
        Assert.Equal("77", update.GetProperty("ticketId").GetString());
        Assert.Equal("Resolved", update.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CloseTicket_WhenTheNoteFails_DoesNotChangeTheStatus()
    {
        var (client, handler) = Create();
        handler.ResponseBody = """{"data":{"createTicketNote":null},"errors":[{"message":"Ticket not found"}]}""";

        await Assert.ThrowsAsync<HttpRequestException>(() => client.CloseTicketAsync(Settings(), "77", "done"));

        Assert.Single(handler.Requests);
    }

    // What the real API sends when a required field is missing: a null message, with the reason under extensions.clientError.
    private const string CategoryRequired = """{"errors":[{"message":null,"extensions":{"clientError":[{"code":"mandatory_validation_failed","param":{"attributes":["category"]}}],"classification":"DataFetchingException"}}],"data":{"updateTicket":null}}""";

    [Fact]
    public async Task CloseTicket_WhenSuperOpsRequiresACategory_RetriesWithTheConfiguredOne()
    {
        var (client, handler) = Create();
        handler.ResponseBodies.Enqueue("""{"data":{"createTicketNote":{"noteId":"1"}}}""");
        handler.ResponseBodies.Enqueue(CategoryRequired);
        handler.ResponseBodies.Enqueue("""{"data":{"updateTicket":{"ticketId":"77"}}}""");

        await client.CloseTicketAsync(Settings(settings => settings.Category = "General Help"), "77", "done");

        Assert.Equal(3, handler.RequestBodies.Count);
        Assert.False(Variables(handler.RequestBodies[1]).TryGetProperty("category", out _)); // the plain change goes first
        Assert.Equal("General Help", Variables(handler.RequestBodies[2]).GetProperty("category").GetString());
        Assert.Equal("Resolved", Variables(handler.RequestBodies[2]).GetProperty("status").GetString());
    }

    [Fact]
    public async Task CloseTicket_WhenACategoryIsRequiredAndNoneIsConfigured_SaysToChooseOne()
    {
        var (client, handler) = Create();
        handler.ResponseBodies.Enqueue("""{"data":{"createTicketNote":{"noteId":"1"}}}""");
        handler.ResponseBodies.Enqueue(CategoryRequired);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.CloseTicketAsync(Settings(), "77", "done"));

        Assert.Contains("required for category", exception.Message);
        Assert.Contains("Choose a Category", exception.Message);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task Picklists_ReadTheNamesOfEachList_AndToleratePartialFailure()
    {
        var (client, handler) = Create();
        handler.ResponseBody = """
            {"data":{"getStatusList":[{"name":"Open"},{"name":"Resolved"}],"getPriorityList":[{"name":"High"}],"getUrgencyList":null,"getCategoryList":[{"name":"Email"}]},
             "errors":[{"message":"urgency unavailable"}]}
            """;

        var picklists = await client.GetPicklistsAsync(Settings());

        Assert.Equal(["Open", "Resolved"], picklists.Statuses);
        Assert.Equal(["High"], picklists.Priorities);
        Assert.Empty(picklists.Urgencies);
        Assert.Equal(["Email"], picklists.Categories);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "API token")]
    [InlineData(HttpStatusCode.Forbidden, "allowed")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limiting")]
    public async Task HttpFailures_SayWhatToCheck_AndNeverEchoTheToken(HttpStatusCode status, string expected)
    {
        var (client, handler) = Create();
        handler.StatusCode = status;

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.ListCompaniesAsync(Settings()));

        Assert.Contains(expected, exception.Message);
        Assert.DoesNotContain("token-abc", exception.Message);
        Assert.Equal(status, exception.StatusCode);
    }
}
