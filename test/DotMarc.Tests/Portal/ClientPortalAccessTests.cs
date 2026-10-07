using System.Net;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
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

    private async Task<int> GroupIdAsync(string name)
    {
        await using var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);
        return await context.Groups.Where(group => group.Name == name).Select(group => group.Id).SingleAsync();
    }

    [Fact]
    public async Task Staff_CanPreviewAGroupsPortal()
    {
        using var client = await SignInAsync("admin");
        var auroraGroupId = await GroupIdAsync(DotMarc.Demo.DemoDataSeeder.ViewerScopedGroupName);

        var html = await client.GetStringAsync($"/portal/preview/{auroraGroupId}");

        Assert.Contains("Preview: this is what clients of Aurora Retail see", html);
        Assert.Contains("Aurora Retail Ltd", html);
        Assert.Contains($"/portal/preview/{auroraGroupId}/domains/aurora-retail.example", html);
    }

    [Fact]
    public async Task Staff_CanPreviewAGroupsDomainPage()
    {
        using var client = await SignInAsync("admin");
        var auroraGroupId = await GroupIdAsync(DotMarc.Demo.DemoDataSeeder.ViewerScopedGroupName);

        var html = await client.GetStringAsync($"/portal/preview/{auroraGroupId}/domains/aurora-retail.example");

        Assert.Contains("Preview: this is what clients of Aurora Retail see", html);
        Assert.Contains("Who sends as this domain", html);
    }

    [Fact]
    public async Task APortalUser_CantUseThePreview()
    {
        using var client = await SignInAsync("client");

        var response = await client.GetAsync("/portal/preview/1");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/portal", response.Headers.Location!.OriginalString.Replace("http://localhost", ""));
        Assert.DoesNotContain("preview", response.Headers.Location!.OriginalString);
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
    public async Task ThePortal_ListsTheClientsDomains_AndNotOthers()
    {
        using var client = await SignInAsync("client");

        var html = await client.GetStringAsync("/portal");

        Assert.Contains("aurora-retail.example", html);
        Assert.DoesNotContain("brightline-legal.example", html);
    }

    [Fact]
    public async Task ThePortal_IsHeadedWithTheClientsBrandedName_AndTitledWithTheProduct()
    {
        using var client = await SignInAsync("client");

        var html = await client.GetStringAsync("/portal");

        Assert.Contains("Aurora Retail Ltd", html);
        Assert.Contains("Your domains - Nova MSP", html);
    }

    [Fact]
    public async Task AnotherGroupsDomainPage_IsNotFound()
    {
        using var client = await SignInAsync("client");

        var html = await client.GetStringAsync("/portal/domains/brightline-legal.example");

        Assert.Contains("find that domain", html); // the apostrophe in "couldn't" is HTML-encoded
        Assert.DoesNotContain("Who sends as this domain", html);
    }

    [Fact]
    public async Task TheClientsOwnDomainPage_ShowsItsSections()
    {
        using var client = await SignInAsync("client");

        var html = await client.GetStringAsync("/portal/domains/aurora-retail.example");

        Assert.Contains("Who sends as this domain", html);
        Assert.Contains("Policy", html);
    }

    [Fact]
    public async Task APortalUser_CanReachBlazorsOwnEndpoints()
    {
        using var client = await SignInAsync("client");

        var response = await client.GetAsync("/_blazor/initializers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task APortalUser_CanSignOut()
    {
        using var client = await SignInAsync("client");
        var html = await client.GetStringAsync("/portal");
        var tokenMatch = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(tokenMatch.Success, "The portal's sign out form has no antiforgery token");

        var response = await client.PostAsync("/signout", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = tokenMatch.Groups[1].Value }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString.Replace("http://localhost", ""));
    }

    [Fact]
    public async Task Staff_StillReachTheDashboard()
    {
        using var client = await SignInAsync("viewer");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/dashboard")).StatusCode);
    }
}
