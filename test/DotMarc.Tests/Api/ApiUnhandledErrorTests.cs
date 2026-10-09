using System.Net;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotMarc.Tests.Api;

/// <summary>An error the API didn't expect still answers in the API's own format, as its OpenAPI document says, rather
/// than with the UI's error page.</summary>
[Collection("Postgres")]
public sealed class ApiUnhandledErrorTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private readonly FailingTimeProvider _timeProvider = new();
    private ApiTestHost _host = null!;

    public ApiUnhandledErrorTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture,
        // Production, where the UI's error page handles exceptions; development shows its own diagnostics page instead.
        new Dictionary<string, string> { ["environment"] = "Production" },
        services => services.AddSingleton<TimeProvider>(_timeProvider));

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task AnUnexpectedError_IsA500Problem_WithoutTheExceptionsDetails()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.GroupsView]);
        using var client = _host.ClientFor(secret);
        _timeProvider.Failing = true;

        var response = await client.GetAsync("/api/v1/groups");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":500", body);
        Assert.DoesNotContain(FailingTimeProvider.Message, body);
    }

    /// <summary>The clock is read on every API request (a key's expiry is checked against it), so failing it fails the
    /// request. It works until a test switches it, so the app can start.</summary>
    private sealed class FailingTimeProvider : TimeProvider
    {
        public const string Message = "The clock broke for this test.";

        public bool Failing { get; set; }

        public override DateTimeOffset GetUtcNow() => Failing ? throw new InvalidOperationException(Message) : base.GetUtcNow();
    }
}
