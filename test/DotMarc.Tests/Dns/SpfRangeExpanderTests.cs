using DotMarc.Dns;
using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class SpfRangeExpanderTests
{
    private sealed class FakeLookup : ITxtRecordLookup
    {
        private readonly Dictionary<string, string[]> _records = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _failing = new(StringComparer.OrdinalIgnoreCase);

        public int Lookups { get; private set; }

        public FakeLookup Spf(string name, string record)
        {
            _records[name] = [record];
            return this;
        }

        public FakeLookup Fail(string name)
        {
            _failing.Add(name);
            return this;
        }

        public Task<IReadOnlyList<string>> GetTxtValuesAsync(string name, CancellationToken cancellationToken)
        {
            Lookups++;
            if (_failing.Contains(name))
            {
                throw new HttpRequestException("resolver down");
            }

            return Task.FromResult<IReadOnlyList<string>>(_records.GetValueOrDefault(name) ?? []);
        }

        public Task<DnsRecordLookupResult> LookupWithCnameAsync(string name, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static Task<IReadOnlyList<SpfServiceRange>?> ExpandAsync(FakeLookup lookup, string domain, string record) =>
        new SpfRangeExpander(lookup).ExpandAsync(domain, SpfRecord.Parse(record), CancellationToken.None);

    [Fact]
    public async Task TheDomainsOwnIp4Term_IsKeptWithNoService()
    {
        var ranges = await ExpandAsync(new FakeLookup(), "example.com", "v=spf1 ip4:192.0.2.0/24 -all");

        Assert.Equal([new SpfServiceRange(null, "example.com", "192.0.2.0/24")], ranges);
    }

    [Fact]
    public async Task AnIncludeOfAKnownService_TagsItsRangesWithTheService()
    {
        var lookup = new FakeLookup().Spf("servers.mcsv.net", "v=spf1 ip4:198.51.100.0/24 ip6:2001:db8::/32 ~all");

        var ranges = await ExpandAsync(lookup, "example.com", "v=spf1 include:servers.mcsv.net -all");

        Assert.Equal(
            [new SpfServiceRange("Mailchimp", "servers.mcsv.net", "198.51.100.0/24"), new SpfServiceRange("Mailchimp", "servers.mcsv.net", "2001:db8::/32")],
            ranges);
    }

    [Fact]
    public async Task ANestedInclude_IsTaggedByTheOutermostKnownHostOnItsPath()
    {
        var lookup = new FakeLookup()
            .Spf("_spf.google.com", "v=spf1 include:_netblocks.google.com ~all")
            .Spf("_netblocks.google.com", "v=spf1 ip4:35.190.247.0/24 ~all");

        var ranges = await ExpandAsync(lookup, "example.com", "v=spf1 include:_spf.google.com ~all");

        Assert.Equal([new SpfServiceRange("Google Workspace", "_spf.google.com", "35.190.247.0/24")], ranges);
    }

    [Fact]
    public async Task AnIncludeOfAnUnknownHost_KeepsItsRangesUnnamed()
    {
        var lookup = new FakeLookup().Spf("mail.vendor.example", "v=spf1 ip4:203.0.113.0/24 -all");

        var ranges = await ExpandAsync(lookup, "example.com", "v=spf1 include:mail.vendor.example -all");

        Assert.Equal([new SpfServiceRange(null, "mail.vendor.example", "203.0.113.0/24")], ranges);
    }

    [Fact]
    public async Task ARedirect_IsFollowedAndTaggedByItsTarget()
    {
        var lookup = new FakeLookup().Spf("sendgrid.net", "v=spf1 ip4:198.51.100.0/24 -all");

        var ranges = await ExpandAsync(lookup, "example.com", "v=spf1 redirect=sendgrid.net");

        Assert.Equal([new SpfServiceRange("SendGrid", "sendgrid.net", "198.51.100.0/24")], ranges);
    }

    [Theory]
    [InlineData("-ip4:192.0.2.0/24")]
    [InlineData("~ip4:192.0.2.0/24")]
    [InlineData("?ip4:192.0.2.0/24")]
    public async Task OnlyAPlusQualifierAuthorises(string term)
    {
        Assert.Empty((await ExpandAsync(new FakeLookup(), "example.com", $"v=spf1 {term} -all"))!);
    }

    [Theory]
    [InlineData("ip4:192.0.2.0/24")]
    [InlineData("+ip4:192.0.2.0/24")]
    public async Task APlusQualifier_WrittenOrImplied_Authorises(string term)
    {
        Assert.Single((await ExpandAsync(new FakeLookup(), "example.com", $"v=spf1 {term} -all"))!);
    }

    [Fact]
    public async Task MalformedTerms_AreSkipped_AndTheirNeighboursKept()
    {
        var ranges = await ExpandAsync(new FakeLookup(), "example.com", "v=spf1 ip4:999.1.1.1 ip4:1.2.3.4/33 ip6:zzz ip4:192.0.2.0/24 ip4: -all");

        Assert.Equal([new SpfServiceRange(null, "example.com", "192.0.2.0/24")], ranges);
    }

    [Fact]
    public async Task AnIncludeLoop_Terminates()
    {
        var lookup = new FakeLookup()
            .Spf("a.example", "v=spf1 ip4:192.0.2.0/24 include:b.example -all")
            .Spf("b.example", "v=spf1 ip4:198.51.100.0/24 include:a.example -all");

        var ranges = await ExpandAsync(lookup, "example.com", "v=spf1 include:a.example -all");

        Assert.Equal(2, ranges!.Count);
    }

    [Fact]
    public async Task ARedirectToItself_Terminates()
    {
        var lookup = new FakeLookup().Spf("example.com", "v=spf1 redirect=example.com");

        Assert.Empty((await ExpandAsync(lookup, "example.com", "v=spf1 redirect=example.com"))!);
    }

    [Fact]
    public async Task AHostileRecordWithManyIncludes_StopsAtTheLookupCap()
    {
        var lookup = new FakeLookup();
        var terms = new List<string>();
        for (var index = 0; index < 80; index++)
        {
            var host = $"host{index}.vendor.example";
            lookup.Spf(host, $"v=spf1 ip4:10.0.{index}.0/24 -all");
            terms.Add($"include:{host}");
        }

        var ranges = await ExpandAsync(lookup, "example.com", $"v=spf1 {string.Join(' ', terms)} -all");

        Assert.Equal(SpfRangeExpander.MaxLookups, lookup.Lookups);
        Assert.Equal(SpfRangeExpander.MaxLookups, ranges!.Count);
    }

    [Fact]
    public async Task ATargetWithNoSpfRecord_ContributesNothing_AndDoesNotFail()
    {
        var ranges = await ExpandAsync(new FakeLookup(), "example.com", "v=spf1 include:nothing.example ip4:192.0.2.0/24 -all");

        Assert.Equal([new SpfServiceRange(null, "example.com", "192.0.2.0/24")], ranges);
    }

    [Fact]
    public async Task AFailingLookup_ReturnsNull()
    {
        var lookup = new FakeLookup().Fail("servers.mcsv.net");

        Assert.Null(await ExpandAsync(lookup, "example.com", "v=spf1 include:servers.mcsv.net -all"));
    }

    [Fact]
    public async Task TheSameRangeByTwoRoutes_IsListedOnce()
    {
        var lookup = new FakeLookup()
            .Spf("sendgrid.net", "v=spf1 ip4:198.51.100.0/24 include:again.sendgrid.net -all")
            .Spf("again.sendgrid.net", "v=spf1 ip4:198.51.100.0/24 -all");

        var ranges = await ExpandAsync(lookup, "example.com", "v=spf1 include:sendgrid.net -all");

        Assert.Single(ranges!);
    }

    [Fact]
    public void Json_RoundTrips()
    {
        var ranges = new[] { new SpfServiceRange("Mailchimp", "servers.mcsv.net", "198.51.100.0/24"), new SpfServiceRange(null, "example.com", "192.0.2.0/24") };

        Assert.Equal(ranges, SpfServiceRanges.Parse(SpfServiceRanges.Serialize(ranges)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"a\":1}")]
    public void Parse_ReturnsEmpty_ForMissingOrMalformedJson(string? json) =>
        Assert.Empty(SpfServiceRanges.Parse(json));
}
