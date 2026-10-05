using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class SpfChangeBuilderTests
{
    private readonly FakeTxtRecordLookup _lookup = new();

    private Task<DnsChangePlan> BuildAsync(string proposed, IReadOnlyList<string> startedFrom) =>
        new SpfChangeBuilder(_lookup, new SpfLookupCounter(_lookup)).BuildAsync(
            new DnsPushRequest(new Domain { Id = 1, Name = "contoso.com" }, "cloudflare", "contoso.com",
                new SpfPushPayload(proposed, SpfEditor.Fingerprint(startedFrom)).Serialize()),
            CancellationToken.None);

    [Fact]
    public async Task SpfChangeBuilder_ReplacesTheLiveSpfValuesOnly()
    {
        _lookup.TxtByName["contoso.com"] = ["google-site-verification=abc", "v=spf1 include:old.example ~all"];
        _lookup.TxtByName["new.example"] = ["v=spf1 a ~all"];

        var plan = await BuildAsync("v=spf1 include:new.example ~all", ["v=spf1 include:old.example ~all"]);

        var change = Assert.Single(plan.Changes);
        Assert.Equal((DnsRecordChangeKind.ReplaceTxtValues, "TXT", "contoso.com", "v=spf1 include:new.example ~all"),
            (change.Kind, change.RecordType, change.Name, change.DesiredValue));
        Assert.Equal(["v=spf1 include:old.example ~all"], change.ValuesToRemove);
    }

    [Fact]
    public async Task SpfChangeBuilder_RefusesWhenTheLiveRecordChanged()
    {
        _lookup.TxtByName["contoso.com"] = ["v=spf1 include:someone-else.example ~all"];

        Assert.Equal("spf-changed", (await BuildAsync("v=spf1 ~all", ["v=spf1 include:old.example ~all"])).Refusal);
    }

    [Fact]
    public async Task SpfChangeBuilder_RefusesARecordOverTheLimit()
    {
        _lookup.TxtByName["contoso.com"] = ["v=spf1 a ~all"];
        var proposed = "v=spf1 " + string.Join(' ', Enumerable.Range(1, 11).Select(number => $"exists:{number}.example")) + " ~all";

        Assert.Equal("spf-too-many-lookups", (await BuildAsync(proposed, ["v=spf1 a ~all"])).Refusal);
    }

    [Fact]
    public async Task SpfChangeBuilder_MergesSeveralLiveRecords()
    {
        _lookup.TxtByName["contoso.com"] = ["v=spf1 a ~all", "v=spf1 mx ~all"];

        var change = Assert.Single((await BuildAsync("v=spf1 a mx ~all", ["v=spf1 a ~all", "v=spf1 mx ~all"])).Changes);

        Assert.Equal(["v=spf1 a ~all", "v=spf1 mx ~all"], change.ValuesToRemove);
        Assert.Equal("v=spf1 a ~all | v=spf1 mx ~all", change.ExistingValue);
    }

    [Fact]
    public async Task SpfChangeBuilder_RefusesAPayloadThatIsntSpf()
    {
        Assert.Equal("invalid", (await BuildAsync("not spf", [])).Refusal);
    }

    [Fact]
    public async Task SpfChangeBuilder_ReportsALookupFailureAsAnError()
    {
        _lookup.ThrowOnQuery = new HttpRequestException("DNS is down");

        Assert.Equal("error", (await BuildAsync("v=spf1 ~all", [])).Refusal);
    }
}
