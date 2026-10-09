using System.Net;
using DotMarc.Data;
using DotMarc.Reporting.ClientReports;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Components;

[Collection("Postgres")]
public sealed class ReportScreensTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private IAsyncDisposable? _cleanup;
    private string _connectionString = "";
    private WebApplicationFactory<Program>? _factory;

    public ReportScreensTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        var connectionString = _connectionString;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DotMarc", connectionString);
            builder.UseSetting("Demo:Enabled", "true");
        });
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

    private async Task<HttpClient> SignInAsync(string persona)
    {
        var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync($"/demo/sign-in/{persona}", content: null);
        return client;
    }

    [Fact]
    public async Task TheSettingsPage_SaysTheDemoDoesntSendEmail()
    {
        using var client = await SignInAsync("admin");

        var html = await client.GetStringAsync("/reports/settings");

        Assert.Contains("Email &amp; reports", html);
        Assert.Contains("The demo doesn", html); // "doesn't", apostrophe encoded
    }

    [Fact]
    public async Task ManageGroups_ShowsAuroraRetailsReportSchedule()
    {
        using var client = await SignInAsync("admin");

        var response = await client.GetAsync("/groups");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Monthly", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ManageGroups_FlagsAGroupWhoseLatestScheduledReportFailed()
    {
        using var client = await SignInAsync("admin"); // boots the demo, which seeds Aurora Retail's schedule
        await using (var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options))
        {
            var auroraId = await context.Groups.Where(group => group.Name == DotMarc.Demo.DemoDataSeeder.ViewerScopedGroupName).Select(group => group.Id).SingleAsync();
            context.ClientReportDeliveries.AddRange(
                new ClientReportDelivery { GroupId = auroraId, PeriodStart = new DateOnly(2026, 1, 1), PeriodEnd = new DateOnly(2026, 1, 31), Kind = ClientReportDeliveryKind.Scheduled, Status = ClientReportDeliveryStatus.Sent },
                new ClientReportDelivery { GroupId = auroraId, PeriodStart = new DateOnly(2026, 2, 1), PeriodEnd = new DateOnly(2026, 2, 28), Kind = ClientReportDeliveryKind.Scheduled, Status = ClientReportDeliveryStatus.Failed, Error = "535 Authentication failed" });
            await context.SaveChangesAsync();
        }

        var html = await client.GetStringAsync("/groups");

        Assert.Contains("Report failed", html);
    }

    [Fact]
    public async Task AViewer_CantOpenTheSettingsPage()
    {
        using var client = await SignInAsync("viewer");

        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/reports/settings")).StatusCode);
    }
}
