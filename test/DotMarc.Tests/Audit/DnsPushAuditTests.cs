using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Audit;

public sealed class DnsPushAuditTests
{
    private static readonly Domain Contoso = new() { Id = 42, Name = "contoso.com" };

    private static readonly IReadOnlyList<DnsRecordChange> OneChange =
    [
        new(DnsRecordChangeKind.Merge, "TXT", "_dmarc.contoso.com", "v=DMARC1; p=none; rua=mailto:reports@dotmarc.example", "v=DMARC1; p=none", "contoso.com"),
    ];

    [Fact]
    public void CreateEntry_RecordsEachRecordsOldAndNewValue()
    {
        var entry = DnsPushAudit.CreateEntry(TestActors.Admin, Contoso, "cloudflare", OneChange, DnsPushOutcome.Pushed);

        Assert.NotNull(entry);
        Assert.Equal((AuditActions.DnsPushed, "42", "contoso.com"), (entry.Action, entry.TargetId, entry.TargetName));
        Assert.Equal("Pushed 1 DNS record for contoso.com to cloudflare", entry.Summary);
        Assert.Equal(
            [new AuditFieldChange("TXT _dmarc.contoso.com", "v=DMARC1; p=none", "v=DMARC1; p=none; rua=mailto:reports@dotmarc.example")],
            entry.Changes);
    }

    [Fact]
    public void CreateEntry_RecordsAReplaceThatFailedAfterDeleting_BecauseTheOldRecordIsGone()
    {
        var entry = DnsPushAudit.CreateEntry(TestActors.Admin, Contoso, "cloudflare", OneChange, DnsPushOutcome.ReplaceFailedAfterDelete);

        Assert.NotNull(entry);
        Assert.Equal(AuditActions.DnsPushFailed, entry.Action);
        Assert.Equal("A DNS push for contoso.com to cloudflare failed after deleting the old record, so the name may now have no record", entry.Summary);
        Assert.Equal(OneChange.Count, entry.Changes.Count);
    }

    [Fact]
    public void CreateEntry_RecordsAProviderError_BecauseEarlierRecordsMayAlreadyBeLive()
    {
        var entry = DnsPushAudit.CreateEntry(TestActors.Admin, Contoso, "cloudflare", OneChange, DnsPushOutcome.ProviderError);

        Assert.NotNull(entry);
        Assert.Equal(AuditActions.DnsPushFailed, entry.Action);
        Assert.Equal("A DNS push for contoso.com to cloudflare failed partway, so some of these records may already be live", entry.Summary);
    }

    [Fact]
    public void CreateEntry_RecordsNothing_WhenTheZoneWasNeverTouched()
    {
        Assert.Null(DnsPushAudit.CreateEntry(TestActors.Admin, Contoso, "cloudflare", OneChange, DnsPushOutcome.ZoneNotFound));
    }

    [Fact]
    public void CreateEntry_NamesAnSpfReplacementAsSpf()
    {
        IReadOnlyList<DnsRecordChange> spf =
        [
            new(DnsRecordChangeKind.ReplaceTxtValues, "TXT", "contoso.com", "v=spf1 include:new.example ~all", "v=spf1 include:old.example ~all", "contoso.com",
                ValuesToRemove: ["v=spf1 include:old.example ~all"]),
        ];

        var entry = DnsPushAudit.CreateEntry(TestActors.Admin, Contoso, "cloudflare", spf, DnsPushOutcome.Pushed);

        Assert.Equal([new AuditFieldChange("SPF contoso.com", "v=spf1 include:old.example ~all", "v=spf1 include:new.example ~all")], entry!.Changes);
    }

    [Fact]
    public void CreateEntry_RecordsAnSpfPushThatLeftADuplicate_AsFailed()
    {
        var entry = DnsPushAudit.CreateEntry(TestActors.Admin, Contoso, "cloudflare", OneChange, DnsPushOutcome.LeftDuplicateRecord);

        Assert.Equal(AuditActions.DnsPushFailed, entry!.Action);
        Assert.Contains("more than one SPF record", entry.Summary);
    }
}
