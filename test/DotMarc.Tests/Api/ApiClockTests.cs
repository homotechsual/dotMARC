using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class ApiClockTests : IAsyncLifetime
{
    // Forty days on, so a report received two days ago (real time) is outside every window.
    private static readonly DateTimeOffset FortyDaysOn = DateTimeOffset.UtcNow.AddDays(40);
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public ApiClockTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() =>
        _host = await ApiTestHost.StartAsync(_fixture, configureServices: services => services.AddSingleton<TimeProvider>(new FixedTimeProvider(FortyDaysOn)));

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task ReportWindows_FollowTheAppsClock()
    {
        var receivedUtc = DateTimeOffset.UtcNow.AddDays(-2);
        var domainId = await _host.SeedDomainAsync("api-clock.example", configure: domain =>
        {
            var report = new Report
            {
                ReportingOrg = "google.com", ReportId = Guid.NewGuid().ToString(), DateRangeBeginUtc = receivedUtc.AddDays(-1),
                DateRangeEndUtc = receivedUtc, RawXml = "<feedback/>", ReceivedUtc = receivedUtc, AuthDetailBackfilledUtc = receivedUtc,
            };
            report.Records.Add(new ReportRecord { SourceIp = "203.0.113.40", MessageCount = 9, Disposition = DispositionResult.None, SpfResult = AuthResult.Pass, DkimResult = AuthResult.Pass, HeaderFrom = "example" });
            domain.Reports.Add(report);
        });
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView], expiresUtc: FortyDaysOn.AddDays(30));
        using var client = _host.ClientFor(secret);

        var summary = await client.GetFromJsonAsync<ApiReportSummary>($"/api/v1/domains/{domainId}/reports/summary");
        var detail = await client.GetFromJsonAsync<ApiDomainDetail>($"/api/v1/domains/{domainId}");

        Assert.Equal(0, summary!.TotalVolume);
        Assert.Null(detail!.PassRate);
    }
}
