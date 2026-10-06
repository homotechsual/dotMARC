using System.Net;
using System.Text.Json;
using DotMarc.Psa;
using DotMarc.Psa.Autotask;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Psa.Autotask;

public sealed class AutotaskClientTests
{
    private const string ZoneResponse = "{\"zoneName\":\"Pre-Release\",\"url\":\"https://webservices2.autotask.net/ATServicesRest/\",\"webUrl\":\"https://ww2.autotask.net/\",\"ci\":0}";

    private static readonly AutotaskSettings Settings = new()
    {
        Username = "api@contoso.com", SecretConfigured = true, IntegrationCodeOverride = "TRACKING", QueueId = 29683354,
        TicketTypeId = 1, IssueTypeId = 12, PriorityId = 2, ClosedStatusId = AutotaskSettings.CompleteStatus,
    };

    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));

    private (AutotaskClient Client, FakeHttpMessageHandler Handler, AutotaskZoneCache Zones) Create()
    {
        var handler = new FakeHttpMessageHandler();
        var secrets = new FakeSecretStore { Secrets = { [AutotaskSettings.SecretStoreKey] = "s3cret" } };
        var zones = new AutotaskZoneCache();
        return (new AutotaskClient(new HttpClient(handler), secrets, zones, _clock), handler, zones);
    }

    [Fact]
    public async Task TheFirstCall_LooksUpTheZone_ThenUsesItWithTheThreeHeaders()
    {
        var (client, handler, zones) = Create();
        handler.ResponseBodies.Enqueue(ZoneResponse);
        handler.ResponseBodies.Enqueue("{\"item\":{\"id\":7,\"status\":1}}");

        await client.GetTicketStatusAsync(Settings, "7");

        Assert.Equal("https://webservices.autotask.net/atservicesrest/v1.0/zoneInformation?user=api%40contoso.com", handler.Requests[0].RequestUri!.ToString());
        var ticketRequest = handler.Requests[1];
        Assert.Equal("https://webservices2.autotask.net/ATServicesRest/V1.0/Tickets/7", ticketRequest.RequestUri!.ToString());
        Assert.Equal("TRACKING", Assert.Single(ticketRequest.Headers.GetValues("ApiIntegrationCode")));
        Assert.Equal("api@contoso.com", Assert.Single(ticketRequest.Headers.GetValues("UserName")));
        Assert.Equal("s3cret", Assert.Single(ticketRequest.Headers.GetValues("Secret")));
        Assert.True(zones.TryGet("api@contoso.com", out var zone));
        Assert.Equal("https://ww2.autotask.net/", zone.WebUrl);
    }

    [Fact]
    public async Task TheZone_IsLookedUpOnlyOnce()
    {
        var (client, handler, _) = Create();
        handler.ResponseBodies.Enqueue(ZoneResponse);
        handler.ResponseBodies.Enqueue("{\"item\":{\"id\":7,\"status\":1}}");
        handler.ResponseBodies.Enqueue("{\"item\":{\"id\":8,\"status\":5}}");

        await client.GetTicketStatusAsync(Settings, "7");
        Assert.Equal(5, await client.GetTicketStatusAsync(Settings, "8"));

        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ARefusedSignIn_ForgetsTheZone_SoTheNextCallLooksItUpAgain()
    {
        var (client, handler, zones) = Create();
        handler.ResponseBodies.Enqueue(ZoneResponse);
        handler.StatusCodes.Enqueue(HttpStatusCode.OK);
        handler.StatusCodes.Enqueue(HttpStatusCode.Unauthorized);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetTicketStatusAsync(Settings, "7"));

        Assert.StartsWith("Autotask refused the sign-in: check the username, secret and API tracking identifier.", exception.Message);
        Assert.False(zones.TryGet("api@contoso.com", out _));
    }

    [Fact]
    public async Task AnUnknownUsername_SaysSo()
    {
        var (client, handler, _) = Create();
        handler.StatusCode = HttpStatusCode.NotFound;

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetTicketStatusAsync(Settings, "7"));

        Assert.Equal("Autotask couldn't find a zone for that username. Check it's the API user's username.", exception.Message);
    }

    [Fact]
    public async Task WithNoTrackingIdentifier_ItRefusesBeforeSendingAnything()
    {
        var (client, handler, _) = Create();
        var withoutCode = new AutotaskSettings { Username = "api@contoso.com", SecretConfigured = true };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetTicketStatusAsync(withoutCode, "7"));

        Assert.Contains("API tracking identifier", exception.Message);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("{\"item\":null}")]
    [InlineData("{}")]
    public async Task AMissingTicket_IsNull(string body)
    {
        var (client, handler, _) = Create();
        handler.ResponseBodies.Enqueue(ZoneResponse);
        handler.ResponseBodies.Enqueue(body);

        Assert.Null(await client.GetTicketStatusAsync(Settings, "7"));
    }

    [Fact]
    public async Task ATicketAnswered404_IsNull()
    {
        var (client, handler, _) = Create();
        handler.ResponseBodies.Enqueue(ZoneResponse);
        handler.StatusCodes.Enqueue(HttpStatusCode.OK);
        handler.StatusCodes.Enqueue(HttpStatusCode.NotFound);

        Assert.Null(await client.GetTicketStatusAsync(Settings, "7"));
    }

    [Fact]
    public async Task Companies_FollowNextPageUrl_AndAreSortedByName()
    {
        var (client, handler, _) = Create();
        handler.ResponseBodies.Enqueue(ZoneResponse);
        handler.ResponseBodies.Enqueue("{\"items\":[{\"id\":30,\"companyName\":\"Woodgrove Bank\"}],\"pageDetails\":{\"nextPageUrl\":\"https://webservices2.autotask.net/ATServicesRest/V1.0/Companies/query/next?paging=abc\"}}");
        handler.ResponseBodies.Enqueue("{\"items\":[{\"id\":31,\"companyName\":\"Contoso Ltd\"}],\"pageDetails\":{\"nextPageUrl\":null}}");

        var companies = await client.ListCompaniesAsync(Settings);

        Assert.Equal([new PsaCompany("31", "Contoso Ltd"), new PsaCompany("30", "Woodgrove Bank")], companies);
        Assert.Contains("Companies/query?search=", handler.Requests[1].RequestUri!.ToString());
        Assert.Contains("isActive", Uri.UnescapeDataString(handler.Requests[1].RequestUri!.Query));
        Assert.Equal("https://webservices2.autotask.net/ATServicesRest/V1.0/Companies/query/next?paging=abc", handler.Requests[2].RequestUri!.ToString());
        Assert.Equal("TRACKING", Assert.Single(handler.Requests[2].Headers.GetValues("ApiIntegrationCode")));
    }

    [Fact]
    public async Task CreateTicket_PostsTheTicket_WithADueDateADayAway()
    {
        var (client, handler, _) = Create();
        handler.ResponseBodies.Enqueue(ZoneResponse);
        handler.ResponseBodies.Enqueue("{\"itemId\":9001}");

        var ticketId = await client.CreateTicketAsync(Settings, new PsaTicketRequest("31", "contoso.io", "MissedReport", "Reports stopped", "No reports for 3 days"));

        Assert.Equal("9001", ticketId);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.EndsWith("/V1.0/Tickets", handler.Requests[1].RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.RequestBodies[1]);
        var root = body.RootElement;
        Assert.Equal(31, root.GetProperty("companyID").GetInt32());
        Assert.Equal("Reports stopped", root.GetProperty("title").GetString());
        Assert.Contains("Domain: contoso.io", root.GetProperty("description").GetString());
        Assert.Equal(29683354, root.GetProperty("queueID").GetInt32());
        Assert.Equal(1, root.GetProperty("ticketType").GetInt32());
        Assert.Equal(12, root.GetProperty("issueType").GetInt32());
        Assert.Equal(2, root.GetProperty("priority").GetInt32());
        Assert.Equal(AutotaskSettings.NewStatus, root.GetProperty("status").GetInt32());
        Assert.Equal(_clock.GetUtcNow().AddDays(1), root.GetProperty("dueDateTime").GetDateTimeOffset());
    }

    [Fact]
    public async Task CreateTicket_WithNoIssueType_LeavesItOut()
    {
        var (client, handler, _) = Create();
        handler.ResponseBodies.Enqueue(ZoneResponse);
        handler.ResponseBodies.Enqueue("{\"itemId\":9001}");
        var withoutIssueType = new AutotaskSettings
        {
            Username = Settings.Username, SecretConfigured = true, IntegrationCodeOverride = "TRACKING", QueueId = 1, TicketTypeId = 1, PriorityId = 2,
        };

        await client.CreateTicketAsync(withoutIssueType, new PsaTicketRequest("31", "contoso.io", "MissedReport", "t", "m"));

        using var body = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.False(body.RootElement.TryGetProperty("issueType", out _));
    }

    [Fact]
    public async Task CloseTicket_PatchesTheStatusAndResolution()
    {
        var (client, handler, _) = Create();
        handler.ResponseBodies.Enqueue(ZoneResponse);
        handler.ResponseBodies.Enqueue("{\"itemId\":9001}");

        await client.CloseTicketAsync(Settings, "9001", "Resolved automatically by dotMARC.");

        Assert.Equal(HttpMethod.Patch, handler.Requests[1].Method);
        Assert.EndsWith("/V1.0/Tickets", handler.Requests[1].RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal(9001, body.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(5, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Resolved automatically by dotMARC.", body.RootElement.GetProperty("resolution").GetString());
    }

    [Fact]
    public async Task Picklists_AreReadFromTheTicketFieldInfo_ActiveValuesOnly()
    {
        var (client, handler, _) = Create();
        handler.ResponseBodies.Enqueue(ZoneResponse);
        handler.ResponseBodies.Enqueue("""
            {"fields":[
              {"name":"queueID","isPickList":true,"picklistValues":[{"value":"29683354","label":"Monitoring","isActive":true},{"value":"1","label":"Old","isActive":false}]},
              {"name":"ticketType","isPickList":true,"picklistValues":[{"value":"1","label":"Service Request","isActive":true}]},
              {"name":"issueType","isPickList":true,"picklistValues":[{"value":"12","label":"Email","isActive":true}]},
              {"name":"priority","isPickList":true,"picklistValues":[{"value":"2","label":"Medium","isActive":true}]},
              {"name":"status","isPickList":true,"picklistValues":[{"value":"1","label":"New","isActive":true},{"value":"5","label":"Complete","isActive":true}]},
              {"name":"title","isPickList":false,"picklistValues":null}
            ]}
            """);

        var picklists = await client.GetTicketPicklistsAsync(Settings);

        Assert.Equal([new PsaOption(29683354, "Monitoring")], picklists.Queues);
        Assert.Equal([new PsaOption(1, "Service Request")], picklists.TicketTypes);
        Assert.Equal([new PsaOption(12, "Email")], picklists.IssueTypes);
        Assert.Equal([new PsaOption(2, "Medium")], picklists.Priorities);
        Assert.Equal([new PsaOption(5, "Complete"), new PsaOption(1, "New")], picklists.Statuses);
        Assert.EndsWith("/V1.0/Tickets/entityInformation/fields", handler.Requests[1].RequestUri!.AbsolutePath);
    }
}
