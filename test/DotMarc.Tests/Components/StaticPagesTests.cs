using DotMarc.Tests.Internal;
using DotMarc.Data;
using DotMarc.Portal;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotMarc.Tests.Components;

/// <summary>Pages anyone can open without signing in are rendered statically: a signed-out visitor can't open a Blazor
/// circuit, so an interactive page would only fail to start one (the browser gets the sign-in page's HTML where it
/// expects JSON).</summary>
[Collection("Postgres")]
public sealed class StaticPagesTests : IAsyncLifetime
{
    // Blazor marks where an interactive server component starts with a comment carrying this.
    private const string InteractiveServerMarker = "\"type\":\"server\"";

    private readonly PostgresContainerFixture _fixture;
    private IAsyncDisposable? _cleanup;
    private WebApplicationFactory<Program>? _factory;

    public StaticPagesTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (var connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
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

    [Theory]
    [InlineData("/demo")]
    [InlineData("/AccessDenied")]
    [InlineData("/SignInProblem?reason=admin-consent&code=AADSTS65001")]
    public async Task APageForSignedOutVisitors_DoesntStartACircuit(string path)
    {
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await client.GetStringAsync(path);

        Assert.DoesNotContain(InteractiveServerMarker, html);
    }

    [Fact]
    public async Task ASignedInPage_IsStillInteractive()
    {
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync("/demo/sign-in/admin", content: null);

        var html = await client.GetStringAsync("/dashboard");

        Assert.Contains(InteractiveServerMarker, html);
    }

    [Theory]
    [InlineData("admin", "/dashboard")]
    [InlineData("client", "/portal")]
    public async Task SponsorLink_InBothLayouts_FollowsTheSavedSetting(string persona, string path)
    {
        const string sponsorUrl = "https://github.com/sponsors/homotechsual";
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync($"/demo/sign-in/{persona}", content: null);

        var html = await client.GetStringAsync(path);
        Assert.Contains(sponsorUrl, html);
        Assert.Contains("Sponsor the project", html);

        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<DotMarcDbContext>>();
        await using var context = await dbFactory.CreateDbContextAsync();
        var settings = await BrandingSettingsService.GetAsync(context);
        settings.ShowSponsorLink = false;
        await BrandingSettingsService.SaveAsync(context, TestActors.Admin, settings);

        Assert.DoesNotContain(sponsorUrl, await client.GetStringAsync(path));

        settings.ShowSponsorLink = true;
        await BrandingSettingsService.SaveAsync(context, TestActors.Admin, settings);

        Assert.Contains(sponsorUrl, await client.GetStringAsync(path));
    }

    [Fact]
    public async Task SponsorToggle_IsOnTheAdminPage_WithTheSupportReminder()
    {
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync("/demo/sign-in/admin", content: null);

        var html = await client.GetStringAsync("/branding/settings");

        Assert.Contains("Show sponsor link", html);
        Assert.Contains("Please support the project if", html);
        Assert.Contains("https://github.com/sponsors/homotechsual", html);

        await client.PostAsync("/demo/sign-in/viewer", content: null);
        var response = await client.GetAsync("/branding/settings");
        Assert.False(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task TheSignInProblemPage_ExplainsMissingAdminConsent_WithAConsentLinkForThisApp()
    {
        await using var factory = _factory!.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("EntraId:TenantId", "contoso-tenant");
            builder.UseSetting("EntraId:ClientId", "client-1");
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await client.GetStringAsync("/SignInProblem?reason=admin-consent&code=AADSTS65001");

        Assert.Contains("administrator", html);
        Assert.Contains("https://login.microsoftonline.com/contoso-tenant/adminconsent?client_id=client-1", html);
        Assert.Contains("AADSTS65001", html);
    }

    [Fact]
    public async Task TheSignInProblemPage_ShowsOnlyACodeThatLooksLikeOne()
    {
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await client.GetStringAsync("/SignInProblem?reason=other&code=%3Cb%3Ecall%20us%3C%2Fb%3E");

        Assert.DoesNotContain("call us", html);
    }
}
