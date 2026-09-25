// test/DotMarc.Tests/Notifications/HaloWebhookEndpointTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class HaloWebhookEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;
    private WebApplicationFactory<Program>? _factory;
    private readonly StatusLookupHalo _statusLookup = new();

    public HaloWebhookEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using (var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options))
        {
            await context.Database.MigrateAsync();
            var settings = await context.HaloPsaSettings.SingleAsync();
            settings.WebhookSecret = "the-webhook-secret";
            settings.ClosedStatusId = 9;
            await context.SaveChangesAsync();
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DotMarc", _connectionString);
            builder.UseSetting("Demo:Enabled", "true");
            builder.ConfigureServices(services => services.AddSingleton<IHaloPsaClient>(_statusLookup));
        });

        // Booting the host with Demo:Enabled=true runs DemoDataSeeder.ResetAsync at startup (see
        // Program.cs), which truncates and reseeds AlertEvents with its own demo dataset - wiping
        // out any AlertEvent added before the host boots. Forcing that boot now (CreateClient
        // triggers it) before seeding the alert this test actually exercises keeps the demo reset
        // from wiping it out from under the test.
        _factory.CreateClient().Dispose();

        await using (var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options))
        {
            context.AlertEvents.Add(new AlertEvent
            {
                DomainName = "contoso.io", AlertType = "MissedReport", Severity = "Warning", Title = "t", Message = "m",
                ExternalTicketProvider = "HaloPSA", ExternalTicketId = "4242"
            });
            await context.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    [Fact]
    public async Task ClosedStatusPayload_ResolvesTheMatchingAlert()
    {
        using var client = _factory!.CreateClient();

        var response = await client.PostAsJsonAsync("/integrations/halopsa/webhook/the-webhook-secret", new { ticket_id = 4242, status_id = 9 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);
        // Demo mode's own baseline dataset seeds three unrelated AlertEvents alongside this
        // test's; filter to the one this test is actually about rather than assuming it's the
        // table's only row.
        var alert = await context.AlertEvents.SingleAsync(a => a.ExternalTicketId == "4242");
        Assert.True(alert.IsResolved);
    }

    private HaloWebhookReceipt LatestReceipt() =>
        _factory!.Services.GetRequiredService<HaloWebhookActivity>().Recent(1).Single();

    [Fact]
    public async Task ClosedStatusPayload_IsRecordedAsHavingResolvedAnAlert()
    {
        using var client = _factory!.CreateClient();

        await client.PostAsJsonAsync("/integrations/halopsa/webhook/the-webhook-secret", new { ticket_id = 4242, status_id = 9 });

        var receipt = LatestReceipt();
        Assert.Equal(HaloWebhookDelivery.ClosedStatus, receipt.Delivery);
        Assert.Equal(4242, receipt.TicketId);
        Assert.Equal(9, receipt.StatusId);
        Assert.True(receipt.ResolvedAnAlert);
    }

    [Fact]
    public async Task ClosedStatusPayload_ForATicketWithNoAlert_IsRecordedAsNotResolvingAnything()
    {
        // This is what the integration test's own ticket looks like: closed, but linked to no alert.
        using var client = _factory!.CreateClient();

        await client.PostAsJsonAsync("/integrations/halopsa/webhook/the-webhook-secret", new { ticket_id = 555, status_id = 9 });

        var receipt = LatestReceipt();
        Assert.Equal(HaloWebhookDelivery.ClosedStatus, receipt.Delivery);
        Assert.False(receipt.ResolvedAnAlert);
    }

    [Fact]
    public async Task UnrelatedStatusChange_IsRecordedAsOtherStatus()
    {
        using var client = _factory!.CreateClient();

        await client.PostAsJsonAsync("/integrations/halopsa/webhook/the-webhook-secret", new { ticket_id = 4242, status_id = 3 });

        var receipt = LatestReceipt();
        Assert.Equal(HaloWebhookDelivery.OtherStatus, receipt.Delivery);
        Assert.Equal(3, receipt.StatusId);
    }

    [Fact]
    public async Task MalformedBody_IsRecordedAsUnreadable()
    {
        using var client = _factory!.CreateClient();

        using var content = new StringContent("this is not json", System.Text.Encoding.UTF8, "application/json");
        await client.PostAsync("/integrations/halopsa/webhook/the-webhook-secret", content);

        Assert.Equal(HaloWebhookDelivery.Unreadable, LatestReceipt().Delivery);
    }

    private async Task<HttpResponseMessage> PostRawAsync(string json)
    {
        using var client = _factory!.CreateClient();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.PostAsync("/integrations/halopsa/webhook/the-webhook-secret", content);
    }

    [Theory]
    [InlineData("""{"object":{"id":4242,"status_id":9,"summary":"x"}}""")]
    [InlineData("""{"id":"3f1c3e9e-0c1d-4e34-9a55-6d2f2b7a9f10","event":"ticket_status_changed","object":{"id":4242,"status":{"id":9,"name":"Closed"}}}""")]
    [InlineData("""{"ticket":{"id":"4242","statusid":9}}""")]
    [InlineData("""{"Data":{"TICKET_ID":4242,"Status_Id":9}}""")]
    public async Task ClosedStatus_IsFoundWhereverHaloPutsIt_AndResolvesTheAlert(string body)
    {
        var response = await PostRawAsync(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = LatestReceipt();
        Assert.Equal(HaloWebhookDelivery.ClosedStatus, receipt.Delivery);
        Assert.Equal(4242, receipt.TicketId);
        Assert.True(receipt.ResolvedAnAlert);
        Assert.Empty(_statusLookup.Lookups);
    }

    [Fact]
    public async Task TheRealHaloTicketStatusChangedPayload_ResolvesTheAlert()
    {
        // The structure Halo actually sends for "ticket status changed" (seen on a live tenant, with the
        // ticket's text and most of its fields left out): the ticket's number at the top level as object_id,
        // and the whole ticket, including status_id, nested under "ticket". Its own top-level id is a GUID.
        var response = await PostRawAsync("""
            {
              "id": "8e25bc7b-e07e-4102-9398-5d6647e1e79d",
              "webhook_id": "53232637-7724-4a19-b126-3c7267a292aa",
              "notification_id": 41,
              "escmsg_id": "a726ee11-acb8-f111-8260-02725cb0e102",
              "event": "ticket status changed",
              "message": "The status has been changed for Ticket ID: 0004242.",
              "object_id": 4242,
              "agent_id": 3,
              "timestamp": "2026-09-25T06:41:08.7895915Z",
              "ticket": {
                "id": 4242,
                "summary": "redacted",
                "status_id": 9,
                "tickettype_id": 25,
                "client_id": 1,
                "team_id": 5,
                "agent_id": 3,
                "customfields": [],
                "hasbeenclosed": true
              }
            }
            """);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = LatestReceipt();
        Assert.Equal(HaloWebhookDelivery.ClosedStatus, receipt.Delivery);
        Assert.Equal(4242, receipt.TicketId);
        Assert.Equal(9, receipt.StatusId);
        Assert.True(receipt.ResolvedAnAlert);
        Assert.Empty(_statusLookup.Lookups);
    }

    [Fact]
    public async Task AnEventOnlyPayload_HasItsStatusLookedUpFromHalo()
    {
        // "Event information only": the ticket's id and the event name, but not the status.
        _statusLookup.Status = 9;

        await PostRawAsync("""{"id":"3f1c3e9e-0c1d-4e34-9a55-6d2f2b7a9f10","event":"ticket_status_changed","object_id":4242,"webhook_id":3}""");

        var receipt = LatestReceipt();
        Assert.Equal(HaloWebhookDelivery.ClosedStatus, receipt.Delivery);
        Assert.Equal(9, receipt.StatusId);
        Assert.True(receipt.ResolvedAnAlert);
        Assert.Equal([4242], _statusLookup.Lookups);
    }

    [Fact]
    public async Task AnEventOnlyPayload_ForAnotherStatus_IsIgnoredAsOtherStatus()
    {
        _statusLookup.Status = 3;

        await PostRawAsync("""{"object_id":4242}""");

        var receipt = LatestReceipt();
        Assert.Equal(HaloWebhookDelivery.OtherStatus, receipt.Delivery);
        Assert.Equal(3, receipt.StatusId);
    }

    [Fact]
    public async Task AnEventOnlyPayload_WhenHaloWontSayTheStatus_IsRecordedWithTheReason()
    {
        _statusLookup.Failure = new HttpRequestException("HaloPSA returned 401 Unauthorized for GET Tickets/4242.");

        var response = await PostRawAsync("""{"object_id":4242}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = LatestReceipt();
        Assert.Equal(HaloWebhookDelivery.StatusUnknown, receipt.Delivery);
        Assert.Equal(4242, receipt.TicketId);
        Assert.Contains("401 Unauthorized", receipt.Detail);
    }

    [Fact]
    public async Task ABodyWithNoTicket_IsRecordedWithTheFieldNamesButNotTheValues()
    {
        var response = await PostRawAsync("""{"id":"3f1c3e9e","event":"something","payload":{"customer":"Secret Client Ltd","note":"private"}}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = LatestReceipt();
        Assert.Equal(HaloWebhookDelivery.Unreadable, receipt.Delivery);
        Assert.Equal("Halo sent: id, event, payload { customer, note }", receipt.Detail);
        Assert.DoesNotContain("Secret Client", receipt.Detail);
    }

    [Fact]
    public async Task ABodyWithNeitherTicketNorStatus_IsUnreadable_NotTicket0Status0()
    {
        // Any body without the old field names used to be recorded as "ticket #0, status 0" (an invented example).
        await PostRawAsync("""{"notification_id":5,"event":"status","timestamp":"2026-09-25T06:41:08Z"}""");

        var receipt = LatestReceipt();
        Assert.Equal(HaloWebhookDelivery.Unreadable, receipt.Delivery);
        Assert.Null(receipt.TicketId);
    }

    private sealed class StatusLookupHalo : IHaloPsaClient
    {
        public int Status { get; set; } = 9;
        public Exception? Failure { get; set; }
        public List<int> Lookups { get; } = [];

        public Task<int> GetTicketStatusAsync(HaloPsaSettings settings, int ticketId, CancellationToken cancellationToken = default)
        {
            Lookups.Add(ticketId);
            return Failure is null ? Task.FromResult(Status) : Task.FromException<int>(Failure);
        }

        public Task<IReadOnlyList<HaloClient>> ListClientsAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<HaloTicketType>> ListTicketTypesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<HaloTicketStatus>> ListStatusesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<HaloPriority>> ListPrioritiesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<HaloAgent>> ListAgentsAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CreateTicketAsync(HaloPsaSettings settings, int haloClientId, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CloseTicketAsync(HaloPsaSettings settings, string ticketId, string note, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task WrongSecret_IsRecordedWithoutAnyTicketDetails()
    {
        using var client = _factory!.CreateClient();

        await client.PostAsJsonAsync("/integrations/halopsa/webhook/wrong-secret", new { ticket_id = 4242, status_id = 9 });

        var receipt = LatestReceipt();
        Assert.Equal(HaloWebhookDelivery.WrongSecret, receipt.Delivery);
        Assert.Null(receipt.TicketId);
        Assert.Null(receipt.StatusId);
    }

    [Fact]
    public async Task WrongSecret_ReturnsNotFound_AndDoesNotResolveAnything()
    {
        using var client = _factory!.CreateClient();

        var response = await client.PostAsJsonAsync("/integrations/halopsa/webhook/wrong-secret", new { ticket_id = 4242, status_id = 9 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);
        Assert.False((await context.AlertEvents.SingleAsync(a => a.ExternalTicketId == "4242")).IsResolved);
    }

    [Fact]
    public async Task UnrelatedStatusChange_ReturnsOk_AndDoesNotResolveAnything()
    {
        using var client = _factory!.CreateClient();

        var response = await client.PostAsJsonAsync("/integrations/halopsa/webhook/the-webhook-secret", new { ticket_id = 4242, status_id = 3 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);
        Assert.False((await context.AlertEvents.SingleAsync(a => a.ExternalTicketId == "4242")).IsResolved);
    }

    [Fact]
    public async Task MalformedBody_WithTheCorrectSecret_ReturnsOk_NotBadRequest()
    {
        using var client = _factory!.CreateClient();

        using var content = new StringContent("this is not json", System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/integrations/halopsa/webhook/the-webhook-secret", content);

        // The secret was right, so the endpoint must never surface a parse failure as an error
        // status - a wrong-looking response here could trigger a retry storm from Halo. It must
        // also not crash the app; nothing throws past the handler.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);
        Assert.False((await context.AlertEvents.SingleAsync(a => a.ExternalTicketId == "4242")).IsResolved);
    }
}
