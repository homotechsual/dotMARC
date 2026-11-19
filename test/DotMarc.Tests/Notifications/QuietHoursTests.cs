using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class QuietHoursTests
{
    private static AlertRule Rule(AlertQuietMode mode, int? startMinutes, int? endMinutes, bool ignoredForCritical = true) => new()
    {
        Kind = AlertTypes.MissedReport, Name = "Rule", Severity = "Warning",
        QuietMode = mode, QuietStartMinutes = startMinutes, QuietEndMinutes = endMinutes, QuietIgnoredForCritical = ignoredForCritical
    };

    private static int At(int hour, int minute = 0) => (hour * 60) + minute;

    private static DateTimeOffset Utc(int hour, int minute = 0, int month = 6, int day = 15, int year = 2027) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(23, 0, QuietDecision.Hold)]
    [InlineData(3, 0, QuietDecision.Hold)]
    [InlineData(22, 0, QuietDecision.Hold)]
    [InlineData(6, 59, QuietDecision.Hold)]
    [InlineData(7, 0, QuietDecision.Deliver)]
    [InlineData(12, 0, QuietDecision.Deliver)]
    [InlineData(21, 59, QuietDecision.Deliver)]
    public void AWindowThatCrossesMidnight_HoldsInsideAndDeliversOutside(int hour, int minute, QuietDecision expected)
    {
        var rule = Rule(AlertQuietMode.Hold, At(22), At(7));

        Assert.Equal(expected, QuietHours.Decide(rule, "Warning", Utc(hour, minute), TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData(9, 0, QuietDecision.Hold)]
    [InlineData(16, 59, QuietDecision.Hold)]
    [InlineData(17, 0, QuietDecision.Deliver)]
    [InlineData(8, 59, QuietDecision.Deliver)]
    public void ASameDayWindow_IncludesItsStartAndExcludesItsEnd(int hour, int minute, QuietDecision expected)
    {
        var rule = Rule(AlertQuietMode.Hold, At(9), At(17));

        Assert.Equal(expected, QuietHours.Decide(rule, "Warning", Utc(hour, minute), TimeZoneInfo.Utc));
    }

    [Fact]
    public void MuteMode_MutesInsideTheWindow()
    {
        Assert.Equal(QuietDecision.Mute, QuietHours.Decide(Rule(AlertQuietMode.Mute, At(22), At(7)), "Warning", Utc(23), TimeZoneInfo.Utc));
    }

    [Fact]
    public void OffMode_AlwaysDelivers_EvenWithATimesSet()
    {
        Assert.Equal(QuietDecision.Deliver, QuietHours.Decide(Rule(AlertQuietMode.Off, At(22), At(7)), "Warning", Utc(23), TimeZoneInfo.Utc));
    }

    [Fact]
    public void AStartEqualToTheEnd_IsNoWindow()
    {
        Assert.Equal(QuietDecision.Deliver, QuietHours.Decide(Rule(AlertQuietMode.Hold, At(8), At(8)), "Warning", Utc(8), TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData(null, 420)]
    [InlineData(1320, null)]
    public void AWindowMissingAnEnd_IsNoWindow(int? start, int? end)
    {
        Assert.Equal(QuietDecision.Deliver, QuietHours.Decide(Rule(AlertQuietMode.Hold, start, end), "Warning", Utc(23), TimeZoneInfo.Utc));
    }

    [Fact]
    public void NoRule_Delivers()
    {
        Assert.Equal(QuietDecision.Deliver, QuietHours.Decide(null, "Warning", Utc(23), TimeZoneInfo.Utc));
    }

    [Fact]
    public void ACriticalAlert_IsDeliveredInsideTheWindow_WhenTheRuleIgnoresQuietHoursForCritical()
    {
        Assert.Equal(QuietDecision.Deliver, QuietHours.Decide(Rule(AlertQuietMode.Hold, At(22), At(7), ignoredForCritical: true), AlertSeverities.Critical, Utc(23), TimeZoneInfo.Utc));
    }

    [Fact]
    public void ACriticalAlert_IsHeldLikeTheRest_WhenTheRuleDoesNotIgnoreQuietHours()
    {
        Assert.Equal(QuietDecision.Hold, QuietHours.Decide(Rule(AlertQuietMode.Hold, At(22), At(7), ignoredForCritical: false), AlertSeverities.Critical, Utc(23), TimeZoneInfo.Utc));
    }

    [Fact]
    public void AWarningAlert_IsStillHeld_WhenTheRuleIgnoresQuietHoursForCritical()
    {
        Assert.Equal(QuietDecision.Hold, QuietHours.Decide(Rule(AlertQuietMode.Hold, At(22), At(7), ignoredForCritical: true), "Warning", Utc(23), TimeZoneInfo.Utc));
    }

    [Fact]
    public void TheWindowIsReadInTheConfiguredTimeZone()
    {
        var rule = Rule(AlertQuietMode.Hold, At(22), At(7));
        var newYork = QuietHours.ZoneFor("America/New_York");

        // 23:00 UTC in June is 19:00 in New York (outside the window); 03:00 UTC is 23:00 there (inside).
        Assert.Equal(QuietDecision.Deliver, QuietHours.Decide(rule, "Warning", Utc(23), newYork));
        Assert.Equal(QuietDecision.Hold, QuietHours.Decide(rule, "Warning", Utc(3), newYork));
    }

    [Fact]
    public void AWindowAcrossASpringForwardDay_FollowsLocalTime()
    {
        var rule = Rule(AlertQuietMode.Hold, At(1), At(2));
        var london = QuietHours.ZoneFor("Europe/London");

        // 01:30 UTC on 2027-03-27 is 01:30 GMT, inside the window. On 2027-03-28 clocks went forward at 01:00 UTC, so 01:30 UTC is 02:30 BST.
        Assert.Equal(QuietDecision.Hold, QuietHours.Decide(rule, "Warning", Utc(1, 30, month: 3, day: 27), london));
        Assert.Equal(QuietDecision.Deliver, QuietHours.Decide(rule, "Warning", Utc(1, 30, month: 3, day: 28), london));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Not/AZone")]
    public void AnUnknownOrBlankTimeZone_IsUtc(string? timeZoneId)
    {
        Assert.Equal(TimeZoneInfo.Utc.Id, QuietHours.ZoneFor(timeZoneId).Id);
        Assert.False(QuietHours.IsValidZone(timeZoneId));
    }

    [Fact]
    public void AKnownTimeZone_IsValid()
    {
        Assert.True(QuietHours.IsValidZone("Europe/London"));
        Assert.True(QuietHours.IsValidZone("UTC"));
    }
}
