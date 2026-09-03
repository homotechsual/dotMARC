// test/DotMarc.Tests/Notifications/HaloWebhookEndpointTests.cs
using System.Net;
using System.Net.Http.Json;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class HaloWebhookEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;
    private WebApplicationFactory<Program>? _factory;

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
        });

        // Booting the host with Demo:Enabled=true runs DemoDataSeeder.ResetAsync at startup (see
        // Program.cs), which truncates and reseeds AlertEvents with its own demo dataset — wiping
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
}
