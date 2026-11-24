using DotMarc.Baselines;
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
        var plan = await new DmarcChangeBuilder(new FakeDmarcTxtLookup(), Graph(), new FakeDnsRecordSettingsReader(), new FakeDmarcTargetReader()).BuildAsync(Request(), CancellationToken.None);

        var change = Assert.Single(plan.Changes);
        Assert.Equal((DnsRecordChangeKind.Create, "TXT", "_dmarc.contoso.com", "v=DMARC1; p=none; rua=mailto:rua@dotmarc.example"),
            (change.Kind, change.RecordType, change.Name, change.DesiredValue));
    }

    [Fact]
    public async Task Dmarc_MergesRua_KeepingOtherTags()
    {
        var lookup = new FakeDmarcTxtLookup { Result = new("v=DMARC1; p=reject; rua=mailto:old@example.com", null) };

        var change = Assert.Single((await new DmarcChangeBuilder(lookup, Graph(), new FakeDnsRecordSettingsReader(), new FakeDmarcTargetReader()).BuildAsync(Request(), CancellationToken.None)).Changes);

        Assert.Equal((DnsRecordChangeKind.Merge, "v=DMARC1; p=reject; rua=mailto:rua@dotmarc.example,mailto:old@example.com"), (change.Kind, change.DesiredValue));
    }

    [Fact]
    public async Task Dmarc_WritesTheMailboxAlone_WhenKeepingOthersIsOff()
    {
        var lookup = new FakeDmarcTxtLookup { Result = new("v=DMARC1; p=reject; rua=mailto:old@example.com", null) };
        var settings = new FakeDnsRecordSettingsReader { Settings = new() { KeepOtherRuaAddresses = false } };

        var change = Assert.Single((await new DmarcChangeBuilder(lookup, Graph(), settings, new FakeDmarcTargetReader()).BuildAsync(Request(), CancellationToken.None)).Changes);

        Assert.Equal("v=DMARC1; p=reject; rua=mailto:rua@dotmarc.example", change.DesiredValue);
    }

    [Fact]
    public async Task Dmarc_AddsTheConfiguredAdditionalAddress_ToANewAndAMergedRecord()
    {
        var settings = new FakeDnsRecordSettingsReader { Settings = new() { AdditionalRuaAddress = "second@tool.example" } };

        var created = Assert.Single((await new DmarcChangeBuilder(new FakeDmarcTxtLookup(), Graph(), settings, new FakeDmarcTargetReader()).BuildAsync(Request(), CancellationToken.None)).Changes);
        var lookup = new FakeDmarcTxtLookup { Result = new("v=DMARC1; p=reject; rua=mailto:old@example.com", null) };
        var merged = Assert.Single((await new DmarcChangeBuilder(lookup, Graph(), settings, new FakeDmarcTargetReader()).BuildAsync(Request(), CancellationToken.None)).Changes);

        Assert.Equal("v=DMARC1; p=none; rua=mailto:rua@dotmarc.example,mailto:second@tool.example", created.DesiredValue);
        Assert.Equal("v=DMARC1; p=reject; rua=mailto:rua@dotmarc.example,mailto:second@tool.example", merged.DesiredValue);
    }

    [Fact]
    public async Task Dmarc_ReplacesADelegatedCname()
    {
        var lookup = new FakeDmarcTxtLookup { Result = new("v=DMARC1; p=none", "contoso.dmarc-service.example") };

        var change = Assert.Single((await new DmarcChangeBuilder(lookup, Graph(), new FakeDnsRecordSettingsReader(), new FakeDmarcTargetReader()).BuildAsync(Request(), CancellationToken.None)).Changes);

        Assert.Equal((DnsRecordChangeKind.Replace, "CNAME"), (change.Kind, change.ExistingRecordType));
    }

    [Fact]
    public async Task Dmarc_AddsARecordBesideAValueThatIsNotDmarc()
    {
        var lookup = new FakeDmarcTxtLookup { Result = new("not dmarc", null) };

        var plan = await new DmarcChangeBuilder(lookup, Graph(), new FakeDnsRecordSettingsReader(), new FakeDmarcTargetReader()).BuildAsync(Request(), CancellationToken.None);

        var change = Assert.Single(plan.Changes);
        Assert.Null(plan.Refusal);
        Assert.Equal((DnsRecordChangeKind.ReplaceTxtValues, "v=DMARC1; p=none; rua=mailto:rua@dotmarc.example"), (change.Kind, change.DesiredValue));
        Assert.Empty(change.ValuesToRemove!);
    }

    private static EffectiveDmarcTarget Effective(DmarcPolicyLevel policy, DmarcPolicyLevel? subdomainPolicy = null) =>
        new(policy, subdomainPolicy ?? policy, DmarcTargetSource.System, [], null, null);

    private static async Task<DnsRecordChange> DmarcChange(string? existingRecord, EffectiveDmarcTarget? target, string? delegatedCname = null)
    {
        var lookup = existingRecord is null && delegatedCname is null ? new FakeDmarcTxtLookup() : new FakeDmarcTxtLookup { Result = new(existingRecord, delegatedCname) };
        var builder = new DmarcChangeBuilder(lookup, Graph(), new FakeDnsRecordSettingsReader(), new FakeDmarcTargetReader { Target = target });
        return Assert.Single((await builder.BuildAsync(Request(), CancellationToken.None)).Changes);
    }

    [Fact]
    public async Task Dmarc_CreatesTheTargetPolicy_ForAMissingRecord() =>
        Assert.Equal("v=DMARC1; p=reject; rua=mailto:rua@dotmarc.example", (await DmarcChange(null, Effective(DmarcPolicyLevel.Reject))).DesiredValue);

    [Fact]
    public async Task Dmarc_MergesRuaAndRaisesABelowTargetRecordInOneChange()
    {
        var change = await DmarcChange("v=DMARC1; p=none; rua=mailto:old@example.com", Effective(DmarcPolicyLevel.Reject));

        Assert.Equal((DnsRecordChangeKind.Merge, "v=DMARC1; p=reject; rua=mailto:rua@dotmarc.example,mailto:old@example.com"), (change.Kind, change.DesiredValue));
    }

    [Fact]
    public async Task Dmarc_OnlyMergesRua_WhenTheRecordIsStricterThanTheTarget() =>
        Assert.Equal(
            "v=DMARC1; p=reject; rua=mailto:rua@dotmarc.example,mailto:old@example.com",
            (await DmarcChange("v=DMARC1; p=reject; rua=mailto:old@example.com", Effective(DmarcPolicyLevel.Quarantine))).DesiredValue);

    [Fact]
    public async Task Dmarc_ReplacesADelegatedCnameWithTheTargetPolicy() =>
        Assert.Equal(
            "v=DMARC1; p=reject; rua=mailto:rua@dotmarc.example",
            (await DmarcChange(null, Effective(DmarcPolicyLevel.Reject), "contoso.dmarc-service.example")).DesiredValue);

    [Fact]
    public async Task Dmarc_AddsTheTargetPolicyBesideAValueThatIsNotDmarc()
    {
        var lookup = new FakeDmarcTxtLookup { Result = new("not dmarc", null) };
        var builder = new DmarcChangeBuilder(lookup, Graph(), new FakeDnsRecordSettingsReader(), new FakeDmarcTargetReader { Target = Effective(DmarcPolicyLevel.Reject) });

        var change = Assert.Single((await builder.BuildAsync(Request(), CancellationToken.None)).Changes);

        Assert.Equal((DnsRecordChangeKind.ReplaceTxtValues, "v=DMARC1; p=reject; rua=mailto:rua@dotmarc.example"), (change.Kind, change.DesiredValue));
        Assert.Empty(change.ValuesToRemove!);
    }

    private static async Task<DnsChangePlan> DmarcPlan(string[] txtValues, EffectiveDmarcTarget? target = null)
    {
        var lookup = new FakeDmarcTxtLookup { Result = new(txtValues[0], null) { TxtValues = txtValues } };
        var builder = new DmarcChangeBuilder(lookup, Graph(), new FakeDnsRecordSettingsReader(), new FakeDmarcTargetReader { Target = target });
        return await builder.BuildAsync(Request(), CancellationToken.None);
    }

    [Fact]
    public async Task Dmarc_ACleanRecordAndNoTarget_HasNoNotes()
    {
        var plan = await DmarcPlan(["v=DMARC1; p=reject; rua=mailto:old@example.com"]);

        Assert.Empty(plan.Notes);
        Assert.Equal("v=DMARC1; p=reject; rua=mailto:rua@dotmarc.example,mailto:old@example.com", Assert.Single(plan.Changes).DesiredValue);
    }

    [Fact]
    public async Task Dmarc_ARepeatedPolicyTag_BecomesOneMergeWithTheRepairedValueAndNotes()
    {
        var plan = await DmarcPlan(["v=DMARC1; p=none; p=reject; rua=mailto:old@example.com"]);

        var change = Assert.Single(plan.Changes);
        Assert.Equal((DnsRecordChangeKind.Merge, "v=DMARC1; p=reject; rua=mailto:rua@dotmarc.example,mailto:old@example.com", "v=DMARC1; p=none; p=reject; rua=mailto:old@example.com"),
            (change.Kind, change.DesiredValue, change.ExistingValue));
        Assert.Equal("Repairs a malformed DMARC record", plan.Notes[0]);
        Assert.Contains(plan.Notes, note => note.Contains("repeated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Dmarc_SeveralRecords_AreReplacedByOneAndNonDmarcValuesAreLeftAlone()
    {
        string[] values = ["v=DMARC1; p=none; rua=mailto:a@example.com", "google-site-verification=abc", "v=DMARC1; p=reject"];

        var plan = await DmarcPlan(values);

        var change = Assert.Single(plan.Changes);
        Assert.Equal(DnsRecordChangeKind.ReplaceTxtValues, change.Kind);
        Assert.Equal(["v=DMARC1; p=none; rua=mailto:a@example.com", "v=DMARC1; p=reject"], change.ValuesToRemove);
        Assert.Contains("p=reject", change.DesiredValue);
        Assert.DoesNotContain("p=none", change.DesiredValue);
        Assert.Equal("Repairs a malformed DMARC record", plan.Notes[0]);
    }

    [Fact]
    public async Task Dmarc_ATargetRaiseOnACleanRecord_ListsItsChangesButNotTheRepairsNote()
    {
        var plan = await DmarcPlan(["v=DMARC1; p=none; rua=mailto:old@example.com"], Effective(DmarcPolicyLevel.Reject));

        Assert.NotEmpty(plan.Notes);
        Assert.DoesNotContain("Repairs a malformed DMARC record", plan.Notes);
        Assert.Contains("p=reject", Assert.Single(plan.Changes).DesiredValue);
    }

    [Fact]
    public async Task Dmarc_ALookAlikeValueBesideAProperRecord_IsRemovedAndNamedInTheNotes()
    {
        string[] values = ["v=DMARC1; p=reject", "v=DMARC1 p=none"];

        var plan = await DmarcPlan(values);

        var change = Assert.Single(plan.Changes);
        Assert.Equal(DnsRecordChangeKind.ReplaceTxtValues, change.Kind);
        Assert.Equal(values, change.ValuesToRemove);
        Assert.Contains("Removes a value that looked like a DMARC record but was malformed: v=DMARC1 p=none", plan.Notes);
    }

    [Fact]
    public async Task Dmarc_NeverLowersEnforcement_WhenTheTargetIsWeaker()
    {
        var plan = await DmarcPlan(["v=DMARC1; p=reject; p=none"], Effective(DmarcPolicyLevel.Quarantine));

        Assert.Contains("p=reject", Assert.Single(plan.Changes).DesiredValue);
        Assert.DoesNotContain("p=none", Assert.Single(plan.Changes).DesiredValue);
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
        IDnsChangeBuilder[] builders = [new DmarcChangeBuilder(new FakeDmarcTxtLookup(), Graph(), new FakeDnsRecordSettingsReader(), new FakeDmarcTargetReader()), new TlsrptChangeBuilder(new FakeTlsrptTxtLookup(), Graph())];

        Assert.IsType<TlsrptChangeBuilder>(builders.Find("tlsrpt"));
        Assert.Null(builders.Find("nope"));
        Assert.Null(builders.Find(null));
    }
}
