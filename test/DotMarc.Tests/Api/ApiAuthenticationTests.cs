using System.Net;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class ApiAuthenticationTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public ApiAuthenticationTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static void AssertProblem(HttpResponseMessage response, HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task AValidKey_CanCallTheApi()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.GroupsView]);
        using var client = _host.ClientFor(secret);

        var response = await client.GetAsync("/api/v1/groups");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NoKey_IsAProblem401_NotARedirect()
    {
        using var client = _host.ClientFor(null);

        var response = await client.GetAsync("/api/v1/groups");

        AssertProblem(response, HttpStatusCode.Unauthorized);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task AnUnknownKey_Is401()
    {
        using var client = _host.ClientFor("dmk_thisisnotarealkeythisisnotarealkeyxx");

        AssertProblem(await client.GetAsync("/api/v1/groups"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnExpiredKey_Is401()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.GroupsView], expiresUtc: DateTimeOffset.UtcNow.AddMinutes(-1));
        using var client = _host.ClientFor(secret);

        AssertProblem(await client.GetAsync("/api/v1/groups"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ARevokedKey_Is401()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.GroupsView], revoked: true);
        using var client = _host.ClientFor(secret);

        AssertProblem(await client.GetAsync("/api/v1/groups"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AKeyWithoutThePermission_Is403()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.TagsView]);
        using var client = _host.ClientFor(secret);

        AssertProblem(await client.GetAsync("/api/v1/groups"), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ASignedInBrowserCookie_DoesNotAuthenticateTheApi()
    {
        using var client = _host.ClientFor(null);
        await client.PostAsync("/demo/sign-in/admin", content: null);

        AssertProblem(await client.GetAsync("/api/v1/groups"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnUnknownApiPath_IsAProblemNotARedirect()
    {
        using var client = _host.ClientFor(null);

        AssertProblem(await client.GetAsync("/api/v1/nothing-here"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task LastUsed_IsRecorded_AndNotRewrittenWithinAMinute()
    {
        var (keyId, secret) = await _host.CreateKeyAsync([Permission.GroupsView]);
        using var client = _host.ClientFor(secret);

        await client.GetAsync("/api/v1/groups");
        DateTimeOffset? firstUse;
        await using (var context = _host.CreateContext())
        {
            firstUse = (await context.ApiKeys.SingleAsync(key => key.Id == keyId)).LastUsedUtc;
        }

        await client.GetAsync("/api/v1/groups");
        await using var verify = _host.CreateContext();

        Assert.NotNull(firstUse);
        Assert.Equal(firstUse, (await verify.ApiKeys.SingleAsync(key => key.Id == keyId)).LastUsedUtc);
    }
}

[Collection("Postgres")]
public sealed class ApiRateLimitTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public ApiRateLimitTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() =>
        _host = await ApiTestHost.StartAsync(_fixture, new Dictionary<string, string> { ["Api:RequestsPerMinute"] = "3" });

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task OverTheLimit_Is429WithRetryAfter_AndOtherKeysAreUnaffected()
    {
        var (_, busySecret) = await _host.CreateKeyAsync([Permission.GroupsView]);
        var (_, quietSecret) = await _host.CreateKeyAsync([Permission.GroupsView]);
        using var busyClient = _host.ClientFor(busySecret);
        using var quietClient = _host.ClientFor(quietSecret);

        for (var request = 0; request < 3; request++)
        {
            Assert.Equal(HttpStatusCode.OK, (await busyClient.GetAsync("/api/v1/groups")).StatusCode);
        }

        var limited = await busyClient.GetAsync("/api/v1/groups");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, (await quietClient.GetAsync("/api/v1/groups")).StatusCode);
    }
}
