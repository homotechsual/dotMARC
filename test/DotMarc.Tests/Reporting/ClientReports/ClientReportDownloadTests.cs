using System.Net;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

[Collection("Postgres")]
public sealed class ClientReportDownloadTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;
    private WebApplicationFactory<Program>? _factory;

    public ClientReportDownloadTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DotMarc", _connectionString);
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

    private async Task<int> GroupIdAsync(string name)
    {
        await using var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);
        return await context.Groups.Where(group => group.Name == name).Select(group => group.Id).SingleAsync();
    }

    [Fact]
    public async Task AnAdmin_DownloadsAPdf()
    {
        using var client = await SignInAsync("admin");
        var groupId = await GroupIdAsync(DotMarc.Demo.DemoDataSeeder.ViewerScopedGroupName);
        var lastMonth = DateTime.UtcNow.AddMonths(-1);
        var start = new DateOnly(lastMonth.Year, lastMonth.Month, 1);

        var response = await client.GetAsync($"/reports/groups/{groupId}/pdf?start={start:yyyy-MM-dd}&end={start.AddMonths(1).AddDays(-1):yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("client")]
    public async Task SomeoneWithoutReportsManage_CantDownload(string persona)
    {
        using var client = await SignInAsync(persona);
        var groupId = await GroupIdAsync(DotMarc.Demo.DemoDataSeeder.ViewerScopedGroupName);

        var response = await client.GetAsync($"/reports/groups/{groupId}/pdf?start=2026-01-01&end=2026-01-31");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ABadRange_IsABadRequest()
    {
        using var client = await SignInAsync("admin");
        var groupId = await GroupIdAsync(DotMarc.Demo.DemoDataSeeder.ViewerScopedGroupName);

        var response = await client.GetAsync($"/reports/groups/{groupId}/pdf?start=2026-03-10&end=2026-03-01");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
