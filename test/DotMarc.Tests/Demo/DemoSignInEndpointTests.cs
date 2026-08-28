// test/DotMarc.Tests/Demo/DemoSignInEndpointTests.cs
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DotMarc.Tests.Demo;

[Collection("Postgres")]
public sealed class DemoSignInEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;
    private WebApplicationFactory<Program>? _factory;

    public DemoSignInEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    [Fact]
    public async Task SigningInAsAdmin_GrantsAccessToTheDashboard()
    {
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var signInResponse = await client.PostAsync("/demo/sign-in/admin", content: null);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, signInResponse.StatusCode);

        var dashboardResponse = await client.GetAsync("/dashboard");
        Assert.Equal(System.Net.HttpStatusCode.OK, dashboardResponse.StatusCode);
    }

    [Fact]
    public async Task SigningInAsViewer_AlsoGrantsAccessToTheDashboard_ButNotManageAccess()
    {
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await client.PostAsync("/demo/sign-in/viewer", content: null);

        var dashboardResponse = await client.GetAsync("/dashboard");
        Assert.Equal(System.Net.HttpStatusCode.OK, dashboardResponse.StatusCode);

        var manageAccessResponse = await client.GetAsync("/access");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, manageAccessResponse.StatusCode);
        Assert.Contains("AccessDenied", manageAccessResponse.Headers.Location!.ToString());
    }

    [Fact]
    public async Task UnknownPersona_ReturnsBadRequest()
    {
        using var client = _factory!.CreateClient();

        var response = await client.PostAsync("/demo/sign-in/superuser", content: null);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}
