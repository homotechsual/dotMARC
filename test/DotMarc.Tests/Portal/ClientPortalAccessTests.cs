using System.Net;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DotMarc.Tests.Portal;

/// <summary>A client portal user is kept inside the portal, whatever their role would otherwise allow, and staff are kept
/// out of it. Uses the demo instance's Demo Client and Demo Viewer personas.</summary>
[Collection("Postgres")]
public sealed class ClientPortalAccessTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;
    private WebApplicationFactory<Program>? _factory;

    public ClientPortalAccessTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    [Theory]
    [InlineData("/")]
    [InlineData("/dashboard")]
    [InlineData("/domains")]
    [InlineData("/groups")]
    [InlineData("/alerts")]
    [InlineData("/alerts/settings")]
    [InlineData("/access")]
    public async Task APortalUser_IsSentToThePortal_FromEveryInternalPage(string path)
    {
        using var client = await SignInAsync("client");

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/portal", response.Headers.Location!.OriginalString.Replace("http://localhost", ""));
    }

    [Fact]
    public async Task APortalUser_CanOpenThePortal()
    {
        using var client = await SignInAsync("client");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/portal")).StatusCode);
    }

    [Fact]
    public async Task APortalUser_CantUseTheApi()
    {
        using var client = await SignInAsync("client");

        var response = await client.GetAsync("/api/v1/domains");

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"Got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task Staff_AreSentFromThePortalToTheDashboard()
    {
        using var client = await SignInAsync("viewer");

        var response = await client.GetAsync("/portal");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/dashboard", response.Headers.Location!.OriginalString.Replace("http://localhost", ""));
    }

    [Fact]
    public async Task Staff_StillReachTheDashboard()
    {
        using var client = await SignInAsync("viewer");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/dashboard")).StatusCode);
    }
}
