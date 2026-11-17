using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DnsPushReviewTests
{
    private const string Zone = "contoso.com";

    [Fact]
    public void ACreate_IsAnAddition_WithNothingRemoved()
    {
        var review = DnsPushReview.From(DnsChangePlan.Of(
            new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", "_dmarc.contoso.com", "v=DMARC1; p=none", null, Zone)));

        Assert.Equal([new DnsReviewRecord("TXT", "_dmarc.contoso.com", "v=DMARC1; p=none")], review.Additions);
        Assert.Empty(review.Removals);
        Assert.Equal(Zone, review.ZoneName);
        Assert.False(review.NothingToChange);
    }

    [Fact]
    public void AMerge_AddsTheNewValue_AndRemovesTheOldOne()
    {
        var review = DnsPushReview.From(DnsChangePlan.Of(
            new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", "_dmarc.contoso.com", "v=DMARC1; rua=mailto:new@x.com", "v=DMARC1; rua=mailto:old@x.com", Zone)));

        Assert.Equal([new DnsReviewRecord("TXT", "_dmarc.contoso.com", "v=DMARC1; rua=mailto:new@x.com")], review.Additions);
        Assert.Equal([new DnsReviewRecord("TXT", "_dmarc.contoso.com", "v=DMARC1; rua=mailto:old@x.com")], review.Removals);
        Assert.Empty(review.LostDelegations);
    }

    [Theory]
    [InlineData("TXT", "v=DMARC1; p=none", "v=DMARC1; p=none")]
    [InlineData("CNAME", "Host.Example.com", "host.example.com")]
    public void AMergeThatChangesNothing_IsNotListed(string type, string existing, string desired)
    {
        var review = DnsPushReview.From(DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Merge, type, "name.contoso.com", desired, existing, Zone)));

        Assert.True(review.NothingToChange);
        Assert.Empty(review.Removals);
    }

    [Fact]
    public void ATxtMergeThatDiffersOnlyInCase_IsStillAChange()
    {
        var review = DnsPushReview.From(DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", "n.contoso.com", "V=DMARC1", "v=DMARC1", Zone)));

        Assert.False(review.NothingToChange);
    }

    [Fact]
    public void ReplacingADelegatedCname_RemovesTheCname_AndFlagsTheLostDelegation()
    {
        var review = DnsPushReview.From(DnsChangePlan.Of(new DnsRecordChange(
            DnsRecordChangeKind.Replace, "TXT", "_dmarc.contoso.com", "v=DMARC1; p=none", "contoso.dmarc-service.example", Zone, ExistingRecordType: "CNAME")));

        var removed = new DnsReviewRecord("CNAME", "_dmarc.contoso.com", "contoso.dmarc-service.example");
        Assert.Equal([removed], review.Removals);
        Assert.Equal([removed], review.LostDelegations);
        Assert.Equal([new DnsReviewRecord("TXT", "_dmarc.contoso.com", "v=DMARC1; p=none")], review.Additions);
    }

    [Fact]
    public void ReplacingATxtWithACname_RemovesTheTxt_WithoutFlaggingADelegation()
    {
        var review = DnsPushReview.From(DnsChangePlan.Of(new DnsRecordChange(
            DnsRecordChangeKind.Replace, "CNAME", "s1._domainkey.contoso.com", "s1.dkim.example", "v=DKIM1; k=rsa; p=AAAA", Zone, ExistingRecordType: "TXT")));

        Assert.Equal([new DnsReviewRecord("TXT", "s1._domainkey.contoso.com", "v=DKIM1; k=rsa; p=AAAA")], review.Removals);
        Assert.Empty(review.LostDelegations);
    }

    [Fact]
    public void ReplacingTxtValues_RemovesEachOldValue_AndAddsTheNewOne()
    {
        var review = DnsPushReview.From(DnsChangePlan.Of(new DnsRecordChange(
            DnsRecordChangeKind.ReplaceTxtValues, "TXT", "contoso.com", "v=spf1 include:a.example -all", "v=spf1 a -all | v=spf1 mx -all", Zone,
            ValuesToRemove: ["v=spf1 a -all", "v=spf1 mx -all"])));

        Assert.Equal([new DnsReviewRecord("TXT", "contoso.com", "v=spf1 include:a.example -all")], review.Additions);
        Assert.Equal(["v=spf1 a -all", "v=spf1 mx -all"], review.Removals.Select(record => record.Content));
    }

    [Fact]
    public void ReplacingTxtValues_WithTheValueAlreadyThere_ChangesNothing()
    {
        var review = DnsPushReview.From(DnsChangePlan.Of(new DnsRecordChange(
            DnsRecordChangeKind.ReplaceTxtValues, "TXT", "contoso.com", "v=spf1 -all", "v=spf1 -all", Zone, ValuesToRemove: ["v=spf1 -all"])));

        Assert.True(review.NothingToChange);
        Assert.Empty(review.Removals);
    }

    [Fact]
    public void AMultiRecordPlan_ListsEveryRecord_InOrder()
    {
        var review = DnsPushReview.From(new DnsChangePlan(
        [
            new DnsRecordChange(DnsRecordChangeKind.Create, "CNAME", "mta-sts.contoso.com", "host.example", null, Zone),
            new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", "asuid.mta-sts.contoso.com", "abc123", null, Zone),
        ], null));

        Assert.Equal(["mta-sts.contoso.com", "asuid.mta-sts.contoso.com"], review.Additions.Select(record => record.Name));
    }

    [Fact]
    public void AnEmptyPlan_HasNothingToChange_AndNoZone()
    {
        var review = DnsPushReview.From(DnsChangePlan.Refuse("error"));

        Assert.True(review.NothingToChange);
        Assert.Null(review.ZoneName);
    }

    [Theory]
    [InlineData("contoso.com", "_dmarc.contoso.com", "_dmarc")]
    [InlineData("contoso.com", "contoso.com", "@")]
    [InlineData("contoso.com.", "_DMARC.Contoso.com.", "_DMARC")]
    [InlineData("contoso.com", "selector1._domainkey.contoso.com", "selector1._domainkey")]
    [InlineData("contoso.com", "contoso.com.evil.net", "contoso.com.evil.net")]
    [InlineData("contoso.com", "_dmarc.fabrikam.com", "_dmarc.fabrikam.com")]
    [InlineData(null, "_dmarc.contoso.com", "_dmarc.contoso.com")]
    public void HostWithinZone_IsRelativeToTheZone_WhenTheNameIsInsideIt(string? zone, string name, string expected) =>
        Assert.Equal(expected, DnsPushReview.HostWithinZone(zone, name));

    [Fact]
    public void ToClipboardText_ListsTypeNameAndValue_TabSeparated_OnePerLine()
    {
        var text = DnsPushReview.ToClipboardText([
            new DnsReviewRecord("TXT", "_dmarc.contoso.com", "v=DMARC1; p=none"),
            new DnsReviewRecord("CNAME", "mta-sts.contoso.com", "host.example.net"),
        ]);

        Assert.Equal(
            $"TXT	_dmarc.contoso.com	v=DMARC1; p=none{Environment.NewLine}CNAME	mta-sts.contoso.com	host.example.net",
            text);
    }
}
