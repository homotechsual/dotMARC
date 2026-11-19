namespace DotMarc.Notifications;

public enum QuietDecision
{
    Deliver,
    Hold,
    Mute
}

/// <summary>Works out whether an alert's notification goes out now, waits, or is muted, from its rule's quiet window. Pure, so every case is a
/// plain unit test. See docs/superpowers/specs/2026-11-19-quiet-hours-escalation-design.md.</summary>
public static class QuietHours
{
    /// <param name="rule">The rule that raised the alert. Null (an alert whose rule was deleted) never has a quiet window.</param>
    /// <param name="zone">The zone the window is read in, from <see cref="ZoneFor"/>.</param>
    public static QuietDecision Decide(AlertRule? rule, string severity, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        if (rule is null || rule.QuietMode == AlertQuietMode.Off || rule.QuietStartMinutes is not { } start || rule.QuietEndMinutes is not { } end || start == end)
        {
            return QuietDecision.Deliver;
        }

        if (severity == AlertSeverities.Critical && rule.QuietIgnoredForCritical)
        {
            return QuietDecision.Deliver;
        }

        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var minuteOfDay = (local.Hour * 60) + local.Minute;
        // [start, end): a start after the end is a window that crosses midnight.
        var inside = start < end ? minuteOfDay >= start && minuteOfDay < end : minuteOfDay >= start || minuteOfDay < end;
        if (!inside)
        {
            return QuietDecision.Deliver;
        }

        return rule.QuietMode == AlertQuietMode.Mute ? QuietDecision.Mute : QuietDecision.Hold;
    }

    /// <summary>The zone for an IANA id. A null, blank or unknown id is UTC, so a bad setting never stops alerts being sent.</summary>
    public static TimeZoneInfo ZoneFor(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    public static bool IsValidZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return false;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
            return true;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>Minutes in a day, for validating the start and end of a window (0 to this minus one).</summary>
    public const int MinutesInDay = 24 * 60;
}
