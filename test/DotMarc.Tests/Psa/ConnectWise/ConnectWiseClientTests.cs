using System.Net;
using System.Text;
using System.Text.Json;
using DotMarc.Psa;
using DotMarc.Psa.ConnectWise;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Psa.ConnectWise;

public sealed class ConnectWiseClientTests
{
    private static ConnectWiseSettings Settings(Action<ConnectWiseSettings>? change = null)
    {
        var settings = new ConnectWiseSettings
        {
            SiteUrl = "api-eu.myconnectwise.net", CompanyId = "contoso", PublicKey = "public", PrivateKeyConfigured = true,
            ClientIdOverride = "client-123", BoardId = 1, StatusId = 16, TypeId = 4, PriorityId = 8, ClosedStatusId = 20,
        };
        change?.Invoke(settings);
        return settings;
    }

    private static (ConnectWiseClient Client, FakeHttpMessageHandler Handler) Create()
    {
        var handler = new FakeHttpMessageHandler();
        var secrets = new FakeSecretStore { Secrets = { [ConnectWiseSettings.PrivateKeySecretKey] = "private" } };
        return (new ConnectWiseClient(new HttpClient(handler), secrets), handler);
    }

    [Fact]
    public async Task EveryRequest_SignsInWithTheCompanyAndKeys_AndSendsTheClientId()
    {
        var (client, handler) = Create();
        handler.ResponseBody = "[]";

        await client.ListPrioritiesAsync(Settings());

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api-eu.myconnectwise.net/v4_6_release/apis/3.0/service/priorities?pageSize=1000", request.RequestUri!.ToString());
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal("contoso+public:private", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
        Assert.Equal("client-123", Assert.Single(request.Headers.GetValues("clientId")));
    }

    [Fact]
    public async Task WithNoOverride_dotMARCsOwnClientIdIsSent()
    {
        var (client, handler) = Create();
        handler.ResponseBody = "[]";

        await client.ListPrioritiesAsync(Settings(settings => settings.ClientIdOverride = null));

        Assert.Equal(ConnectWiseSettings.DefaultClientId, Assert.Single(Assert.Single(handler.Requests).Headers.GetValues("clientId")));
    }

    [Fact]
    public async Task WithNoPrivateKeySaved_ItRefusesBeforeSendingAnything()
    {
        var handler = new FakeHttpMessageHandler();
        var client = new ConnectWiseClient(new HttpClient(handler), new FakeSecretStore());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListPrioritiesAsync(Settings()));

        Assert.Contains("private key", exception.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Companies_ArePagedUntilAShortPage_AndSortedByName()
    {
        var (client, handler) = Create();
        var fullPage = "[" + string.Join(",", Enumerable.Range(1, 1000).Select(number => $"{{\"id\":{number},\"name\":\"Company {number:0000}\"}}")) + "]";
        handler.ResponseBodies.Enqueue(fullPage);
        handler.ResponseBodies.Enqueue("[{\"id\":1001,\"name\":\"Aardvark Ltd\"}]");

        var companies = await client.ListCompaniesAsync(Settings());

        Assert.Equal(1001, companies.Count);
        Assert.Equal(new PsaCompany("1001", "Aardvark Ltd"), companies[0]);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("conditions=deletedFlag%3Dfalse", handler.Requests[0].RequestUri!.Query);
        Assert.Contains("page=2", handler.Requests[1].RequestUri!.Query);
    }

    [Fact]
    public async Task Boards_AndEachBoardsStatusesAndTypes_AreListed()
    {
        var (client, handler) = Create();
        handler.ResponseBodies.Enqueue("[{\"id\":2,\"name\":\"Projects\"},{\"id\":1,\"name\":\"Help Desk\"}]");
        handler.ResponseBodies.Enqueue("[{\"id\":16,\"name\":\"New\"}]");
        handler.ResponseBodies.Enqueue("[{\"id\":4,\"name\":\"Email\"}]");

        Assert.Equal([new PsaOption(1, "Help Desk"), new PsaOption(2, "Projects")], await client.ListBoardsAsync(Settings()));
        Assert.Equal([new PsaOption(16, "New")], await client.ListBoardStatusesAsync(Settings(), 1));
        Assert.Equal([new PsaOption(4, "Email")], await client.ListBoardTypesAsync(Settings(), 1));

        Assert.Equal("/v4_6_release/apis/3.0/service/boards", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("conditions=inactiveFlag%3Dfalse", handler.Requests[0].RequestUri!.Query);
        Assert.Equal("/v4_6_release/apis/3.0/service/boards/1/statuses", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("/v4_6_release/apis/3.0/service/boards/1/types", handler.Requests[2].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task CreateTicket_PostsTheTicketWithTheSavedDefaults()
    {
        var (client, handler) = Create();
        handler.ResponseBody = "{\"id\":4321}";

        var ticketId = await client.CreateTicketAsync(Settings(), new PsaTicketRequest("250", "contoso.io", "MissedReport", new string('x', 150), "Reports stopped"));

        Assert.Equal("4321", ticketId);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.EndsWith("/service/tickets", handler.Requests[0].RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        var root = body.RootElement;
        Assert.Equal(100, root.GetProperty("summary").GetString()!.Length);
        Assert.Contains("Domain: contoso.io", root.GetProperty("initialDescription").GetString());
        Assert.Equal(250, root.GetProperty("company").GetProperty("id").GetInt32());
        Assert.Equal(1, root.GetProperty("board").GetProperty("id").GetInt32());
        Assert.Equal(16, root.GetProperty("status").GetProperty("id").GetInt32());
        Assert.Equal(4, root.GetProperty("type").GetProperty("id").GetInt32());
        Assert.Equal(8, root.GetProperty("priority").GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task CreateTicket_WithNoType_LeavesTypeOut()
    {
        var (client, handler) = Create();
        handler.ResponseBody = "{\"id\":1}";

        await client.CreateTicketAsync(Settings(settings => settings.TypeId = null), new PsaTicketRequest("250", "contoso.io", "MissedReport", "t", "m"));

        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.False(body.RootElement.TryGetProperty("type", out _));
    }

    [Fact]
    public async Task GetTicket_ReadsTheClosedFlagAndStatus_AndAMissingTicketIsNull()
    {
        var (client, handler) = Create();
        handler.ResponseBodies.Enqueue("{\"id\":4321,\"closedFlag\":true,\"status\":{\"id\":20,\"name\":\">Closed\"}}");
        handler.StatusCodes.Enqueue(HttpStatusCode.OK);
        handler.StatusCodes.Enqueue(HttpStatusCode.NotFound);

        Assert.Equal(new ConnectWiseTicket(true, 20), await client.GetTicketAsync(Settings(), "4321"));
        Assert.Null(await client.GetTicketAsync(Settings(), "9999"));
        Assert.Contains("fields=id,closedFlag,status", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task CloseTicket_AddsAnInternalNote_ThenPatchesTheStatus()
    {
        var (client, handler) = Create();
        handler.ResponseBody = "{}";

        await client.CloseTicketAsync(Settings(), "4321", "Resolved automatically by dotMARC.");

        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("/service/tickets/4321/notes", handler.Requests[0].RequestUri!.AbsolutePath);
        using (var note = JsonDocument.Parse(handler.RequestBodies[0]))
        {
            Assert.Equal("Resolved automatically by dotMARC.", note.RootElement.GetProperty("text").GetString());
            Assert.True(note.RootElement.GetProperty("internalAnalysisFlag").GetBoolean());
        }

        Assert.Equal(HttpMethod.Patch, handler.Requests[1].Method);
        Assert.EndsWith("/service/tickets/4321", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("[{\"op\":\"replace\",\"path\":\"status\",\"value\":{\"id\":20}}]", handler.RequestBodies[1]);
    }

    [Fact]
    public async Task ARefusedSignIn_SaysWhatToCheck_WithoutRepeatingTheKey()
    {
        var (client, handler) = Create();
        handler.StatusCode = HttpStatusCode.Unauthorized;
        handler.ResponseBody = "{\"message\":\"Invalid credentials\"}";

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.ListPrioritiesAsync(Settings()));

        Assert.StartsWith("ConnectWise refused the sign-in: check the company ID, public key and private key.", exception.Message);
        Assert.Contains("Invalid credentials", exception.Message);
        Assert.DoesNotContain("private\"", exception.Message);
        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }
}
