using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DnsPlanFingerprintTests
{
    private static DnsRecordChange Merge(string desiredValue = "v=DMARC1; p=none; rua=mailto:reports@dotmarc.example", string? existingValue = "v=DMARC1; p=none", string zoneName = "contoso.com") =>
        new(DnsRecordChangeKind.Merge, "TXT", "_dmarc.contoso.com", desiredValue, existingValue, zoneName);

    [Fact]
    public void TheSameChanges_GiveTheSameFingerprint_EvenFromSeparatelyBuiltLists()
    {
        var first = DnsPlanFingerprint.Of([Merge(), Merge(zoneName: "contoso.com")]);
        var second = DnsPlanFingerprint.Of([Merge(), Merge(zoneName: "contoso.com")]);

        Assert.Equal(first, second);
    }

    [Fact]
    public void TheFingerprint_IsLowercaseHexOfASha256()
    {
        var fingerprint = DnsPlanFingerprint.Of([Merge()]);

        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
    }

    [Fact]
    public void AChangedDesiredValue_ExistingValue_Kind_Zone_OrRecordedValuesToRemove_EachChangeTheFingerprint()
    {
        var baseline = DnsPlanFingerprint.Of([Merge()]);

        Assert.NotEqual(baseline, DnsPlanFingerprint.Of([Merge(desiredValue: "v=DMARC1; p=reject")]));
        Assert.NotEqual(baseline, DnsPlanFingerprint.Of([Merge(existingValue: "v=DMARC1; p=quarantine")]));
        Assert.NotEqual(baseline, DnsPlanFingerprint.Of([Merge(existingValue: null)]));
        Assert.NotEqual(baseline, DnsPlanFingerprint.Of([Merge(zoneName: "other.com")]));
        Assert.NotEqual(baseline, DnsPlanFingerprint.Of([Merge() with { Kind = DnsRecordChangeKind.Create }]));
        Assert.NotEqual(baseline, DnsPlanFingerprint.Of([Merge() with { RecordType = "CNAME" }]));
        Assert.NotEqual(baseline, DnsPlanFingerprint.Of([Merge() with { Name = "_dmarc.other.com" }]));
        Assert.NotEqual(baseline, DnsPlanFingerprint.Of([Merge() with { ExistingRecordType = "CNAME" }]));
        Assert.NotEqual(baseline, DnsPlanFingerprint.Of([Merge() with { ValuesToRemove = ["v=spf1 -all"] }]));
    }

    [Fact]
    public void AnExtraChange_OrADifferentOrder_ChangesTheFingerprint()
    {
        var first = Merge(desiredValue: "first");
        var second = Merge(desiredValue: "second");

        Assert.NotEqual(DnsPlanFingerprint.Of([first]), DnsPlanFingerprint.Of([first, second]));
        Assert.NotEqual(DnsPlanFingerprint.Of([first, second]), DnsPlanFingerprint.Of([second, first]));
    }

    [Fact]
    public void FieldsThatMerelyShareACharacterBoundary_DoNotCollide()
    {
        var splitEarly = new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", "ab", "c", null, "z");
        var splitLate = new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", "a", "bc", null, "z");

        Assert.NotEqual(DnsPlanFingerprint.Of([splitEarly]), DnsPlanFingerprint.Of([splitLate]));
    }

    [Fact]
    public void AnEmptyList_HasAFixedFingerprint_AndNullAndEmptyValuesToRemoveAreTheSame()
    {
        Assert.Equal(DnsPlanFingerprint.Of([]), DnsPlanFingerprint.Of([]));
        Assert.Matches("^[0-9a-f]{64}$", DnsPlanFingerprint.Of([]));
        Assert.Equal(
            DnsPlanFingerprint.Of([Merge() with { ValuesToRemove = null }]),
            DnsPlanFingerprint.Of([Merge() with { ValuesToRemove = [] }]));
    }
}
