using DotMarc.Data;
using DotMarc.Reporting;

namespace DotMarc.Notifications;

/// <summary>The pass rate over the window, and whether it is low enough to alert.</summary>
public sealed record PassRateResult(bool Raise, int Messages, double PassPercent);

/// <summary>Message volume in the last window and the one before it. <see cref="ChangePercent"/> is signed: negative for a drop.</summary>
public sealed record VolumeResult(bool Raise, int Current, int Previous, double ChangePercent);

/// <summary>Messages rejected or quarantined in the window.</summary>
public sealed record RejectedResult(bool Raise, int Messages);

/// <summary>Decides the three metric alerts from a domain's reports. Pure: the time is passed in. A report counts in a window when it
/// was received after the window's start and no later than its end, so a report exactly one window back belongs to the earlier window.
/// Every count is of messages (each record's message count), never of reports or records.</summary>
public static class MetricAlertEvaluator
{
    public static PassRateResult PassRate(IEnumerable<Report> reports, int windowDays, int belowPercent, int minimumMessages, DateTimeOffset nowUtc)
    {
        var records = RecordsIn(reports, nowUtc.AddDays(-windowDays), nowUtc).ToList();
        var messages = records.Sum(record => (long)record.MessageCount);
        var passing = records.Where(DomainStatistics.IsPassing).Sum(record => (long)record.MessageCount);
        var passPercent = messages == 0 ? 0 : passing * 100.0 / messages;
        var raise = messages >= minimumMessages && messages > 0 && passing * 100 < belowPercent * messages;
        return new PassRateResult(raise, (int)messages, passPercent);
    }

    /// <summary>The current window is the last <paramref name="windowDays"/> days and the earlier one the same length before it. It raises only
    /// when the earlier period had at least <paramref name="minimumMessages"/>, so a quiet domain never alerts. A direction other than
    /// rise or either is read as drop.</summary>
    public static VolumeResult Volume(IEnumerable<Report> reports, int windowDays, string direction, int changePercent, int minimumMessages, DateTimeOffset nowUtc)
    {
        var all = reports.ToList();
        var current = RecordsIn(all, nowUtc.AddDays(-windowDays), nowUtc).Sum(record => (long)record.MessageCount);
        var previous = RecordsIn(all, nowUtc.AddDays(-2 * windowDays), nowUtc.AddDays(-windowDays)).Sum(record => (long)record.MessageCount);
        if (previous == 0)
        {
            return new VolumeResult(false, (int)current, 0, 0);
        }

        var change = (current - previous) * 100.0 / previous;
        var dropped = current * 100 <= previous * (100 - changePercent);
        var rose = current * 100 >= previous * (100 + changePercent);
        var moved = direction switch
        {
            "rise" => rose,
            "either" => dropped || rose,
            _ => dropped
        };
        return new VolumeResult(previous >= minimumMessages && moved, (int)current, (int)previous, change);
    }

    public static RejectedResult Rejected(IEnumerable<Report> reports, int windowDays, int messagesAtLeast, DateTimeOffset nowUtc)
    {
        var messages = RecordsIn(reports, nowUtc.AddDays(-windowDays), nowUtc)
            .Where(record => record.Disposition is DispositionResult.Reject or DispositionResult.Quarantine)
            .Sum(record => (long)record.MessageCount);
        return new RejectedResult(messages >= messagesAtLeast, (int)messages);
    }

    private static IEnumerable<ReportRecord> RecordsIn(IEnumerable<Report> reports, DateTimeOffset startExclusive, DateTimeOffset endInclusive) =>
        reports
            .Where(report => report.ReceivedUtc > startExclusive && report.ReceivedUtc <= endInclusive)
            .SelectMany(report => report.Records);
}
