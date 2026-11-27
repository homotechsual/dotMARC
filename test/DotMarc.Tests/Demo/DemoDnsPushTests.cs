using DotMarc.Demo;
using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.Demo;

public sealed class DemoDnsPushTests
{
    [Fact]
    public async Task DemoAuthorizationReturnsOnlyToTheCallbackAndPreservesState()
    {
        var provider = new DemoDnsPushProvider("cloudflare", new());
        var url = new Uri(await provider.BuildAuthorizationUrlAsync("signed+state/value", "challenge", "https://demo.example/dns-push/cloudflare/callback"));
        Assert.Equal("demo.example", url.Host);
        Assert.Equal("/dns-push/cloudflare/callback", url.AbsolutePath);
        Assert.Equal("signed+state/value", Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(url.Query)["state"]);
    }

    [Fact]
    public async Task DemoPushUpdatesOnlySimulatedRecordsAndFailuresDoNotWrite()
    {
        var records = new DemoDnsRecords();
        var provider = new DemoDnsPushProvider("cloudflare", records);
        var exchange = await provider.ExchangeAsync("demo-code", "", "", CancellationToken.None);
        var change = new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", "_dmarc.test.example", "v=DMARC1; p=none", null, "test.example");
        Assert.Equal(DnsPushOutcome.Pushed, (await provider.PushAsync(exchange.Session!, [change], CancellationToken.None)).Outcome);
        Assert.Equal(change.DesiredValue, records.Get("TXT", change.Name));
        var failed = change with { Name = "_dmarc.broken.example", ZoneName = "broken.example" };
        Assert.Equal(DnsPushOutcome.ProviderError, (await provider.PushAsync(exchange.Session!, [failed], CancellationToken.None)).Outcome);
        Assert.Null(records.Get("TXT", failed.Name));
        var other = new DemoDnsPushProvider("azure-dns", records);
        Assert.Equal(DnsPushOutcome.ProviderError, (await other.PushAsync(exchange.Session!, [change], CancellationToken.None)).Outcome);
        records.Clear();
        Assert.Null(records.Get("TXT", change.Name));
    }
}
