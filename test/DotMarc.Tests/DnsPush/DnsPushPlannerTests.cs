using DotMarc.Data;
using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DnsPushPlannerTests
{
    private sealed class FakeDetector(Func<string, DnsProviderDetectionResult> detect) : IDnsProviderDetector
    {
        public List<string> Detected { get; } = [];

        public Task<DnsProviderDetectionResult> DetectAsync(string domainName, CancellationToken cancellationToken)
        {
            Detected.Add(domainName);
            return Task.FromResult(detect(domainName));
        }
    }

    private sealed class FakeBuilder(bool writesToDomainZone = true, string? refusal = null) : IDnsChangeBuilder
    {
        public string Target => "dmarc";
        public string RequiredPolicy => "DomainsEdit";
        public bool WritesToDomainZone => writesToDomainZone;
        public List<DnsPushRequest> Requests { get; } = [];

        public Task<DnsChangePlan> BuildAsync(DnsPushRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(refusal is null
                ? DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"_dmarc.{request.Domain.Name}", "v=DMARC1; p=none", null, request.DomainZone ?? "mailbox.example"))
                : DnsChangePlan.Refuse(refusal));
        }
    }

    private static Domain ContosoDomain() => new() { Name = "mail.contoso.com", FirstSeenUtc = DateTimeOffset.UtcNow };

    private static DnsProviderDetectionResult Found(DetectedDnsProvider provider, string zoneName) => new(provider, zoneName, ["ns1.example"]);

    [Fact]
    public async Task WhenTheDomainIsOnTheProviderBeingPushedTo_ThePlanIsBuiltWithTheDetectedZone()
    {
        var detector = new FakeDetector(_ => Found(DetectedDnsProvider.Cloudflare, "contoso.com"));
        var builder = new FakeBuilder();

        var result = await new DnsPushPlanner(detector).PlanAsync(ContosoDomain(), builder, "cloudflare", "payload", CancellationToken.None);

        Assert.Null(result.Flag);
        Assert.Equal("contoso.com", Assert.Single(result.Plan!.Changes).ZoneName);
        var request = Assert.Single(builder.Requests);
        Assert.Equal(("cloudflare", "contoso.com", "payload"), (request.Provider, request.DomainZone, request.Payload));
        Assert.Equal(["mail.contoso.com"], detector.Detected);
    }

    [Fact]
    public async Task WhenTheDomainsDnsHasMovedToAnotherProvider_ItIsZoneNotFound_AndTheBuilderIsNeverCalled()
    {
        var detector = new FakeDetector(_ => Found(DetectedDnsProvider.AzureDns, "contoso.com"));
        var builder = new FakeBuilder();

        var result = await new DnsPushPlanner(detector).PlanAsync(ContosoDomain(), builder, "cloudflare", null, CancellationToken.None);

        Assert.Equal("zone-not-found", result.Flag);
        Assert.Null(result.Plan);
        Assert.Empty(builder.Requests);
    }

    [Fact]
    public async Task WhenNoProviderIsRecognised_ItIsZoneNotFound()
    {
        var detector = new FakeDetector(_ => Found(DetectedDnsProvider.Unknown, "contoso.com"));

        var result = await new DnsPushPlanner(detector).PlanAsync(ContosoDomain(), new FakeBuilder(), "cloudflare", null, CancellationToken.None);

        Assert.Equal("zone-not-found", result.Flag);
    }

    [Fact]
    public async Task AFailedLookup_IsAGenericError_NotZoneNotFound()
    {
        var detector = new FakeDetector(_ => throw new HttpRequestException("dns is down"));
        var builder = new FakeBuilder();

        var result = await new DnsPushPlanner(detector).PlanAsync(ContosoDomain(), builder, "cloudflare", null, CancellationToken.None);

        Assert.Equal("error", result.Flag);
        Assert.Null(result.Plan);
        Assert.Empty(builder.Requests);
    }

    [Fact]
    public async Task ABuildersOwnRefusal_BecomesTheFlag()
    {
        var detector = new FakeDetector(_ => Found(DetectedDnsProvider.Cloudflare, "contoso.com"));

        var result = await new DnsPushPlanner(detector).PlanAsync(ContosoDomain(), new FakeBuilder(refusal: "spf-multiple"), "cloudflare", null, CancellationToken.None);

        Assert.Equal("spf-multiple", result.Flag);
        Assert.Null(result.Plan);
    }

    [Fact]
    public async Task ABuilderThatDoesntWriteToTheDomainsZone_SkipsDetection_AndGetsANullZone()
    {
        var detector = new FakeDetector(_ => throw new InvalidOperationException("must not be called"));
        var builder = new FakeBuilder(writesToDomainZone: false);

        var result = await new DnsPushPlanner(detector).PlanAsync(ContosoDomain(), builder, "cloudflare", null, CancellationToken.None);

        Assert.Null(result.Flag);
        Assert.NotNull(result.Plan);
        Assert.Empty(detector.Detected);
        Assert.Null(Assert.Single(builder.Requests).DomainZone);
    }

    [Fact]
    public async Task TheProviderKeyComparisonIgnoresCase()
    {
        var detector = new FakeDetector(_ => Found(DetectedDnsProvider.GoogleCloudDns, "contoso.com"));

        var result = await new DnsPushPlanner(detector).PlanAsync(ContosoDomain(), new FakeBuilder(), "Google-Cloud-DNS", null, CancellationToken.None);

        Assert.Null(result.Flag);
    }
}
