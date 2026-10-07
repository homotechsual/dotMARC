using System.Net;
using System.Text;
using DotMarc.Data;
using DotMarc.Portal;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Portal;

/// <summary>Logos are served without sign-in, cached for good, and an SVG can't run anything when opened directly.</summary>
[Collection("Postgres")]
public sealed class BrandingLogoEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;
    private WebApplicationFactory<Program>? _factory;

    public BrandingLogoEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DotMarc", _connectionString);
            builder.UseSetting("Demo:Enabled", "true");
        });

        // Booting with Demo:Enabled=true resets the demo dataset, so boot now, before the tests store a logo.
        _factory.CreateClient().Dispose();
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

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    [Fact]
    public async Task ALogo_IsServedWithLongCaching_AndAnSvgIsLockedDown()
    {
        Guid imageId;
        await using (var context = CreateContext())
        {
            var upload = await BrandingSettingsService.UploadImageAsync(context, TestActors.Admin, Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"));
            imageId = upload.ImageId!.Value;
        }

        using var client = _factory!.CreateClient();
        var response = await client.GetAsync($"/branding/logo/{imageId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.CacheControl!.Public);
        Assert.Equal(TimeSpan.FromDays(365), response.Headers.CacheControl.MaxAge);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Contains("default-src 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
    }

    [Fact]
    public async Task AnUnknownLogo_IsNotFound()
    {
        using var client = _factory!.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/branding/logo/{Guid.NewGuid()}")).StatusCode);
    }
}
