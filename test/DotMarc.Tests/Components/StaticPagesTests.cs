using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Mvc.Testing;
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
}
