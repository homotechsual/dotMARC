using DotMarc.Dns;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class SpfEditorTests
{
    private static SpfLookupCount Count(int total) => new(total, [], [], []);

    [Fact]
    public void StartingPoint_IsTheLiveRecord_WhenThereIsOne()
    {
        var (record, isMerge) = SpfEditor.StartingPoint(["v=spf1 include:a.example -all"], ["spf.protection.outlook.com"], '~');

        Assert.Equal("v=spf1 include:a.example -all", record.Format());
        Assert.False(isMerge);
    }

    [Fact]
    public void StartingPoint_MergesSeveralRecords_AndAddsTheDefaultEndingIfNoneHasOne()
    {
        var (record, isMerge) = SpfEditor.StartingPoint(["v=spf1 include:a.example", "v=spf1 include:b.example"], [], '-');

        Assert.Equal("v=spf1 include:a.example include:b.example -all", record.Format());
        Assert.True(isMerge);
    }

    [Fact]
    public void StartingPoint_WithNoRecord_IncludesTheSuggestedServices()
    {
        var (record, _) = SpfEditor.StartingPoint([], ["spf.protection.outlook.com", "_spf.google.com"], '~');

        Assert.Equal("v=spf1 include:spf.protection.outlook.com include:_spf.google.com ~all", record.Format());
    }

    [Fact]
    public void Fingerprint_IgnoresOrder_ButNotContent()
    {
        Assert.Equal(SpfEditor.Fingerprint(["v=spf1 a ~all", "v=spf1 mx ~all"]), SpfEditor.Fingerprint(["v=spf1 mx ~all", "v=spf1 a ~all"]));
        Assert.NotEqual(SpfEditor.Fingerprint(["v=spf1 a ~all"]), SpfEditor.Fingerprint(["v=spf1 a -all"]));
    }

    [Fact]
    public void BlockReason_RefusesARecordOverTheLimit()
    {
        Assert.Equal("spf-too-many-lookups", SpfEditor.BlockReason("v=spf1 a ~all", Count(11), ["v=spf1 mx ~all"], Count(1)));
    }

    [Fact]
    public void BlockReason_AllowsLoweringARecordAlreadyOverTheLimit()
    {
        Assert.Null(SpfEditor.BlockReason("v=spf1 a ~all", Count(12), ["v=spf1 mx ~all"], Count(15)));
        Assert.Equal("spf-too-many-lookups", SpfEditor.BlockReason("v=spf1 a ~all", Count(15), ["v=spf1 mx ~all"], Count(15)));
    }

    [Fact]
    public void BlockReason_RefusesPushingTheLiveRecordUnchanged()
    {
        Assert.Equal("nothing-to-push", SpfEditor.BlockReason("v=spf1 a ~all", Count(1), ["v=spf1  a ~all"], Count(1)));
        Assert.Null(SpfEditor.BlockReason("v=spf1 a ~all", Count(1), ["v=spf1 a ~all", "v=spf1 mx ~all"], Count(1)));
    }

    [Fact]
    public void SuggestedIncludes_FollowTheDetectedServices()
    {
        var detected = new[] { new DetectedMailService("Microsoft 365", DetectedMailServiceKind.Inbox), new DetectedMailService("SendGrid", DetectedMailServiceKind.Sending) };

        Assert.Equal(["spf.protection.outlook.com", "sendgrid.net"], SpfIncludeCatalog.SuggestedIncludes(detected));
    }
}
