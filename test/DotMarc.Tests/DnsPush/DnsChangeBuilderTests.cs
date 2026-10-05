using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.Graph;
using DotMarc.MtaSts;
using DotMarc.Tests.Internal;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DnsChangeBuilderTests
{
    private static readonly Domain Contoso = new() { Id = 1, Name = "contoso.com" };

    private static IOptions<GraphOptions> Graph(string? tlsrptMailbox = "tlsrpt@dotmarc.example") => Options.Create(new GraphOptions
    {
        ClientId = "client", TenantId = "tenant", ClientSecret = "secret", MailboxAddress = "rua@dotmarc.example", TlsrptMailboxAddress = tlsrptMailbox,
    });

    private static DnsPushRequest Request(string provider = "cloudflare") => new(Contoso, provider, "contoso.com", null);

    [Fact]
    public async Task Dmarc_CreatesARecord_WhenThereIsNone()
    {
        var plan = await new DmarcChangeBuilder(new FakeDmarcTxtLookup(), Graph()).BuildAsync(Request(), CancellationToken.None);

        var change = Assert.Single(plan.Changes);
        Assert.Equal((DnsRecordChangeKind.Create, "TXT", "_dmarc.contoso.com", "v=DMARC1; p=none; rua=mailto:rua@dotmarc.example"),
            (change.Kind, change.RecordType, change.Name, change.DesiredValue));
    }

    [Fact]
    public async Task Dmarc_MergesRua_KeepingOtherTags()
    {
        var lookup = new FakeDmarcTxtLookup { Result = new("v=DMARC1; p=reject; rua=mailto:old@example.com", null) };

        var change = Assert.Single((await new DmarcChangeBuilder(lookup, Graph()).BuildAsync(Request(), CancellationToken.None)).Changes);

        Assert.Equal((DnsRecordChangeKind.Merge, "v=DMARC1; p=reject; rua=mailto:rua@dotmarc.example"), (change.Kind, change.DesiredValue));
    }

    [Fact]
    public async Task Dmarc_ReplacesADelegatedCname()
    {
        var lookup = new FakeDmarcTxtLookup { Result = new("v=DMARC1; p=none", "contoso.dmarc-service.example") };

        var change = Assert.Single((await new DmarcChangeBuilder(lookup, Graph()).BuildAsync(Request(), CancellationToken.None)).Changes);

        Assert.Equal((DnsRecordChangeKind.Replace, "CNAME"), (change.Kind, change.ExistingRecordType));
    }

    [Fact]
    public async Task Dmarc_RefusesARecordItCantMergeInto()
    {
        var lookup = new FakeDmarcTxtLookup { Result = new("not dmarc", null) };

        Assert.Equal("unmergeable", (await new DmarcChangeBuilder(lookup, Graph()).BuildAsync(Request(), CancellationToken.None)).Refusal);
    }

    [Fact]
    public async Task Tlsrpt_RefusesWithoutATlsrptMailbox()
    {
        Assert.Equal("error", (await new TlsrptChangeBuilder(new FakeTlsrptTxtLookup(), Graph(tlsrptMailbox: null)).BuildAsync(Request(), CancellationToken.None)).Refusal);
    }

    [Fact]
    public async Task Tlsrpt_CreatesARecord_WhenThereIsNone()
    {
        var change = Assert.Single((await new TlsrptChangeBuilder(new FakeTlsrptTxtLookup(), Graph()).BuildAsync(Request(), CancellationToken.None)).Changes);

        Assert.Equal(("_smtp._tls.contoso.com", "v=TLSRPTv1; rua=mailto:tlsrpt@dotmarc.example"), (change.Name, change.DesiredValue));
    }

    [Fact]
    public async Task DmarcAuthorization_WritesToTheMailboxDomainsZone()
    {
        var detector = new FakeDnsProviderDetector { Result = new(DetectedDnsProvider.Cloudflare, "dotmarc.example", []) };
        var lookup = new FakeDmarcAuthorizationTxtLookup();
        var builder = new DmarcAuthorizationChangeBuilder(lookup, detector, Graph());

        var change = Assert.Single((await builder.BuildAsync(Request(), CancellationToken.None)).Changes);

        Assert.False(builder.WritesToDomainZone);
        Assert.Equal(("contoso.com._report._dmarc.dotmarc.example", "v=DMARC1;", "dotmarc.example"), (change.Name, change.DesiredValue, change.ZoneName));
    }

    [Fact]
    public async Task DmarcAuthorization_RefusesWhenTheMailboxDomainIsElsewhere()
    {
        var detector = new FakeDnsProviderDetector { Result = new(DetectedDnsProvider.AzureDns, "dotmarc.example", []) };

        var plan = await new DmarcAuthorizationChangeBuilder(new FakeDmarcAuthorizationTxtLookup(), detector, Graph()).BuildAsync(Request("cloudflare"), CancellationToken.None);

        Assert.Equal("zone-not-found", plan.Refusal);
    }

    [Fact]
    public async Task MtaSts_CreatesTheCname()
    {
        var builder = new MtaStsChangeBuilder(Options.Create(new MtaStsOptions { HostingHostname = "mta-sts.dotmarc.example" }), new FakeMtaStsCnameLookup(), new FakeMtaStsHostProvisioner());

        var change = Assert.Single((await builder.BuildAsync(Request(), CancellationToken.None)).Changes);

        Assert.Equal((DnsRecordChangeKind.Create, "CNAME", "mta-sts.contoso.com", "mta-sts.dotmarc.example"), (change.Kind, change.RecordType, change.Name, change.DesiredValue));
        Assert.Equal("MtaStsManage", builder.RequiredPolicy);
    }

    [Fact]
    public async Task MtaSts_RefusesWithoutAHostingHostname()
    {
        var builder = new MtaStsChangeBuilder(Options.Create(new MtaStsOptions()), new FakeMtaStsCnameLookup(), new FakeMtaStsHostProvisioner());

        Assert.Equal("error", (await builder.BuildAsync(Request(), CancellationToken.None)).Refusal);
    }

    [Fact]
    public void Find_LooksBuildersUpByTarget()
    {
        IDnsChangeBuilder[] builders = [new DmarcChangeBuilder(new FakeDmarcTxtLookup(), Graph()), new TlsrptChangeBuilder(new FakeTlsrptTxtLookup(), Graph())];

        Assert.IsType<TlsrptChangeBuilder>(builders.Find("tlsrpt"));
        Assert.Null(builders.Find("nope"));
        Assert.Null(builders.Find(null));
    }
}
