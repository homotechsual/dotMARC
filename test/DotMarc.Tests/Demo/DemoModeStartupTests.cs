// test/DotMarc.Tests/Demo/DemoModeStartupTests.cs
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotMarc.Tests.Demo;

[Collection("Postgres")]
public sealed class DemoModeStartupTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DemoModeStartupTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private WebApplicationFactory<Program> CreateFactory(bool demoEnabled) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DotMarc", _connectionString);
            builder.UseSetting("Demo:Enabled", demoEnabled ? "true" : "false");
        });

    [Fact]
    public async Task StartsSuccessfully_WithNoGraphOrEntraIdConfiguration_WhenDemoModeIsEnabled()
    {
        await using var factory = CreateFactory(demoEnabled: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/demo");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RedirectsUnauthenticatedRequests_ToTheDemoPicker_WhenDemoModeIsEnabled()
    {
        await using var factory = CreateFactory(demoEnabled: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/demo", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task DemoSignInEndpoint_DoesNotExist_WhenDemoModeIsDisabled()
    {
        // Regression guard for the auth-bypass risk this endpoint would otherwise be: it must
        // be completely unreachable in the real (non-demo) app.
        //
        // This can't be observed through a raw HTTP round-trip here: the app's FallbackPolicy
        // (RequireAuthenticatedUser) intercepts every unauthenticated request — including one to
        // a path with no matching endpoint at all — before ASP.NET Core's routing would ever get
        // a chance to return a literal 404. With a placeholder, unresolvable EntraId tenant, that
        // interception itself throws while trying to challenge via OpenIdConnect (confirmed by
        // capturing full server-side logs during investigation: AuthorizationMiddleware ->
        // ChallengeAsync -> OpenIdConnectHandler -> IDX20803 "Unable to obtain configuration"),
        // producing a 500 instead of a 404. That failure mode is pre-existing, Task-5-independent
        // app behavior (the same thing happens for *any* unauthenticated request to *any*
        // nonexistent path once EntraId is configured, whether or not this endpoint exists), so
        // asserting on the resulting HTTP status code wouldn't actually verify what this guard
        // needs to verify. Inspecting the compiled endpoint list directly instead is a reliable,
        // HTTP-pipeline-independent way to confirm the mapping itself is genuinely absent — see
        // task-5-report.md for the two confirming runs (disabled: route list has no
        // "demo/sign-in" entry; enabled: it does).
        await using var factory = CreateFactory(demoEnabled: false).WithWebHostBuilder(builder =>
        {
            // The real (non-demo) app requires Graph/EntraId config to start; provide the
            // minimum placeholder values so the host builds far enough to route the request —
            // ValidateOnStart only rejects missing values, not unreachable ones.
            builder.UseSetting("Graph:ClientId", "placeholder");
            builder.UseSetting("Graph:TenantId", "placeholder");
            builder.UseSetting("Graph:ClientSecret", "placeholder");
            builder.UseSetting("Graph:MailboxAddress", "placeholder@example.com");
            builder.UseSetting("EntraId:TenantId", "placeholder");
            builder.UseSetting("EntraId:ClientId", "placeholder");
            builder.UseSetting("EntraId:ClientSecret", "placeholder");
        });

        var endpointDataSources = factory.Services.GetRequiredService<IEnumerable<EndpointDataSource>>();
        var routePatterns = endpointDataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText);

        Assert.DoesNotContain(routePatterns, pattern => pattern is not null && pattern.Contains("demo/sign-in", StringComparison.OrdinalIgnoreCase));
    }
}
