using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>What the Alerts page says about an alert's notification when quiet hours kept it back.</summary>
public sealed class AlertNotificationStateTests
{
    private static readonly DateTimeOffset RaisedAtUtc = new(2027, 6, 15, 23, 0, 0, TimeSpan.Zero);

    private static AlertRule Rule(AlertQuietMode mode) => new()
    {
        Kind = AlertTypes.MissedReport, Name = "Rule", Severity = "Warning", QuietMode = mode, QuietStartMinutes = 22 * 60, QuietEndMinutes = 7 * 60
    };

    private static AlertEvent Alert(bool resolved = false, DateTimeOffset? notifiedUtc = null, DateTimeOffset? raisedUtc = null) => new()
    {
        DomainName = "contoso.io", AlertType = AlertTypes.MissedReport, Severity = "Warning", Title = "t", Message = "m",
        CreatedUtc = raisedUtc ?? RaisedAtUtc, NotifiedUtc = notifiedUtc, IsResolved = resolved
    };

    [Fact]
    public void AnOpenAlertNotYetNotified_IsHeld()
    {
        Assert.Equal("Notification held until the quiet window ends.", AlertRulePresentation.DescribeNotification(Alert(), Rule(AlertQuietMode.Hold), TimeZoneInfo.Utc));
    }

    [Fact]
    public void AnAlertRaisedInsideAMuteWindow_IsMuted()
    {
        Assert.Equal("Muted by quiet hours.", AlertRulePresentation.DescribeNotification(Alert(notifiedUtc: RaisedAtUtc), Rule(AlertQuietMode.Mute), TimeZoneInfo.Utc));
    }

    [Fact]
    public void AnAlertRaisedOutsideAMuteWindow_SaysNothing_BecauseItWasSent()
    {
        var raisedAtNoon = new DateTimeOffset(2027, 6, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(AlertRulePresentation.DescribeNotification(Alert(notifiedUtc: raisedAtNoon, raisedUtc: raisedAtNoon), Rule(AlertQuietMode.Mute), TimeZoneInfo.Utc));
    }

    [Fact]
    public void AResolvedAlert_SaysNothing_EvenIfItWasNeverSent()
    {
        Assert.Null(AlertRulePresentation.DescribeNotification(Alert(resolved: true), Rule(AlertQuietMode.Hold), TimeZoneInfo.Utc));
    }

    [Fact]
    public void AnAlertAlreadyNotified_SaysNothing()
    {
        Assert.Null(AlertRulePresentation.DescribeNotification(Alert(notifiedUtc: RaisedAtUtc), Rule(AlertQuietMode.Hold), TimeZoneInfo.Utc));
    }

    [Fact]
    public void WithoutItsRule_AnAlertNotYetNotified_IsStillHeld_ButNothingElseIsGuessed()
    {
        Assert.Equal("Notification held until the quiet window ends.", AlertRulePresentation.DescribeNotification(Alert(), null, TimeZoneInfo.Utc));
        Assert.Null(AlertRulePresentation.DescribeNotification(Alert(notifiedUtc: RaisedAtUtc), null, TimeZoneInfo.Utc));
    }
}
