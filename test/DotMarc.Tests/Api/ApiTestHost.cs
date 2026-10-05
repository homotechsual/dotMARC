using System.Net.Http.Headers;
using DotMarc.Data;
using DotMarc.Security;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using MudBlazor;

namespace DotMarc.Tests.Api;

/// <summary>Boots the app against its own database for API tests. Demo mode stands in for Entra (as in the Halo
/// webhook tests); the host is booted before anything is seeded because demo start-up resets the data. Names seeded
/// here are unique so the demo dataset never collides with them.</summary>
internal sealed class ApiTestHost : IAsyncDisposable
{
    private readonly IAsyncDisposable _databaseCleanup;

    private ApiTestHost(string connectionString, IAsyncDisposable databaseCleanup, WebApplicationFactory<Program> factory)
    {
        ConnectionString = connectionString;
        _databaseCleanup = databaseCleanup;
        Factory = factory;
    }

    public string ConnectionString { get; }
    public WebApplicationFactory<Program> Factory { get; }

    public static async Task<ApiTestHost> StartAsync(PostgresContainerFixture fixture, IReadOnlyDictionary<string, string>? settings = null)
    {
        var (connectionString, databaseCleanup) = await fixture.CreateDatabaseAsync();
        await using (var context = CreateContext(connectionString))
        {
            await context.Database.MigrateAsync();
        }

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DotMarc", connectionString);
            builder.UseSetting("Demo:Enabled", "true");
            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                builder.UseSetting(key, value);
            }
        });
        factory.CreateClient().Dispose();
        return new ApiTestHost(connectionString, databaseCleanup, factory);
    }

    public DotMarcDbContext CreateContext() => CreateContext(ConnectionString);

    private static DotMarcDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connectionString).Options);

    /// <summary>Inserts a key directly (not through the service) so tests can make expired, revoked and AccessManage
    /// keys. With <paramref name="scopedGroupIds"/> the key's role is scopable and limited to those groups.</summary>
    public async Task<(int KeyId, string Secret)> CreateKeyAsync(IReadOnlyList<Permission> permissions, IReadOnlyList<int>? scopedGroupIds = null,
        DateTimeOffset? expiresUtc = null, bool revoked = false, string name = "test key")
    {
        var secret = ApiKeySecrets.Generate();
        var nowUtc = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        var groups = scopedGroupIds is null ? [] : await context.Groups.Where(group => scopedGroupIds.Contains(group.Id)).ToListAsync();
        var apiKey = new ApiKey
        {
            Name = $"{name} {Guid.NewGuid():N}",
            Prefix = ApiKeySecrets.DisplayPrefix(secret),
            Hash = ApiKeySecrets.Hash(secret),
            Role = new Role { Name = $"role {Guid.NewGuid():N}", IsScopable = scopedGroupIds is not null, Permissions = [.. permissions] },
            ScopedGroups = groups,
            CreatedBy = "Test Admin",
            CreatedUtc = nowUtc,
            ExpiresUtc = expiresUtc ?? nowUtc.AddDays(90),
            RevokedUtc = revoked ? nowUtc : null,
        };
        context.ApiKeys.Add(apiKey);
        await context.SaveChangesAsync();
        return (apiKey.Id, secret);
    }

    public HttpClient ClientFor(string? secret)
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (secret is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }

        return client;
    }

    public async Task<int> SeedGroupAsync(string name)
    {
        await using var context = CreateContext();
        var group = new Group { Name = name };
        context.Groups.Add(group);
        await context.SaveChangesAsync();
        return group.Id;
    }

    public async Task<int> SeedTagAsync(string name)
    {
        await using var context = CreateContext();
        var tag = new Tag { Name = name, Color = Color.Primary };
        context.Tags.Add(tag);
        await context.SaveChangesAsync();
        return tag.Id;
    }

    public async Task<int> SeedDomainAsync(string name, IReadOnlyList<int>? groupIds = null, IReadOnlyList<int>? tagIds = null,
        bool monitored = true, Action<Domain>? configure = null)
    {
        await using var context = CreateContext();
        var domain = new Domain { Name = name, IsMonitored = monitored, FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-10) };
        domain.Groups = groupIds is null ? [] : await context.Groups.Where(group => groupIds.Contains(group.Id)).ToListAsync();
        domain.Tags = tagIds is null ? [] : await context.Tags.Where(tag => tagIds.Contains(tag.Id)).ToListAsync();
        configure?.Invoke(domain);
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
        return domain.Id;
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _databaseCleanup.DisposeAsync();
    }
}
