using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Audit;

public sealed class DnsPushAuditTests
{
    [Fact]
    public void CreateEntry_RecordsEachRecordsOldAndNewValue()
    {
        var domain = new Domain { Id = 42, Name = "contoso.com" };
        IReadOnlyList<DnsRecordChange> changes =
        [
            new(DnsRecordChangeKind.Merge, "TXT", "_dmarc.contoso.com", "v=DMARC1; p=none; rua=mailto:reports@dotmarc.example", "v=DMARC1; p=none", "contoso.com"),
        ];

        var entry = DnsPushAudit.CreateEntry(TestActors.Admin, domain, "cloudflare", changes);

        Assert.Equal((AuditActions.DnsPushed, "42", "contoso.com"), (entry.Action, entry.TargetId, entry.TargetName));
        Assert.Equal("Pushed 1 DNS record for contoso.com to cloudflare", entry.Summary);
        Assert.Equal(
            [new AuditFieldChange("TXT _dmarc.contoso.com", "v=DMARC1; p=none", "v=DMARC1; p=none; rua=mailto:reports@dotmarc.example")],
            entry.Changes);
    }
}
