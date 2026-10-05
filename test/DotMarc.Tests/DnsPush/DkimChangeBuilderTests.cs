using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DkimChangeBuilderTests
{
    private readonly FakeTxtRecordLookup _lookup = new();

    private Task<DnsChangePlan> BuildAsync(params DomainDkimRecord[] records)
    {
        var domain = new Domain { Id = 1, Name = "contoso.com", DkimSelectors = records.Select(record => record.Selector).ToList(), DkimRecords = [.. records] };
        return new DkimChangeBuilder(_lookup).BuildAsync(new DnsPushRequest(domain, "cloudflare", "contoso.com", null), CancellationToken.None);
    }

    private static DomainDkimRecord Cname(string selector, string target) => new() { Selector = selector, RecordType = DkimRecordType.Cname, Value = target };
    private static DomainDkimRecord Txt(string selector, string key) => new() { Selector = selector, RecordType = DkimRecordType.Txt, Value = key };

    [Fact]
    public async Task CreatesWhatIsMissing_AndSkipsWhatMatches()
    {
        _lookup.LookupsByName["selector2._domainkey.contoso.com"] = new("v=DKIM1; p=KEY", "selector2-target.example.");

        var plan = await BuildAsync(Cname("selector1", "selector1-target.example"), Cname("selector2", "selector2-target.example"));

        var change = Assert.Single(plan.Changes);
        Assert.Equal((DnsRecordChangeKind.Create, "CNAME", "selector1._domainkey.contoso.com", "selector1-target.example"),
            (change.Kind, change.RecordType, change.Name, change.DesiredValue));
    }

    [Fact]
    public async Task UpdatesACnamePointingElsewhere()
    {
        _lookup.LookupsByName["selector1._domainkey.contoso.com"] = new(null, "old-target.example.");

        var change = Assert.Single((await BuildAsync(Cname("selector1", "selector1-target.example"))).Changes);

        Assert.Equal((DnsRecordChangeKind.Merge, "old-target.example"), (change.Kind, change.ExistingValue));
    }

    [Fact]
    public async Task ReplacesACnameWithAnExpectedTxt_AndTheOtherWayRound()
    {
        _lookup.LookupsByName["google._domainkey.contoso.com"] = new("v=DKIM1; p=OLD", "somewhere.example.");
        _lookup.LookupsByName["selector1._domainkey.contoso.com"] = new("v=DKIM1; p=OLD", null);

        var plan = await BuildAsync(Txt("google", "v=DKIM1; p=NEW"), Cname("selector1", "selector1-target.example"));

        Assert.Equal(
            [("TXT", "CNAME"), ("CNAME", "TXT")],
            plan.Changes.Select(change => (change.RecordType, change.ExistingRecordType!)));
        Assert.All(plan.Changes, change => Assert.Equal(DnsRecordChangeKind.Replace, change.Kind));
    }

    [Fact]
    public async Task UpdatesADifferentKey_AndIgnoresWhitespace()
    {
        _lookup.LookupsByName["google._domainkey.contoso.com"] = new("v=DKIM1; p=OLD", null);
        _lookup.LookupsByName["zoho._domainkey.contoso.com"] = new("v=DKIM1; p=SAME KEY", null);

        var plan = await BuildAsync(Txt("google", "v=DKIM1; p=NEW"), Txt("zoho", "v=DKIM1; p=SAMEKEY"));

        var change = Assert.Single(plan.Changes);
        Assert.Equal((DnsRecordChangeKind.Merge, "google._domainkey.contoso.com"), (change.Kind, change.Name));
    }

    [Fact]
    public async Task RefusesWhenEverythingMatches()
    {
        _lookup.LookupsByName["google._domainkey.contoso.com"] = new("v=DKIM1; p=KEY", null);

        Assert.Equal("nothing-to-push", (await BuildAsync(Txt("google", "v=DKIM1; p=KEY"))).Refusal);
    }
}
