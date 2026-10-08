using DotMarc.Reporting.ClientReports;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

public sealed class ReportPeriodTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void ThePreviousWeek_RunsMondayToSunday()
    {
        var period = ReportPeriods.Previous(ReportFrequency.Weekly, TimeZoneInfo.Utc, Utc(2026, 3, 11)); // a Wednesday

        Assert.Equal((new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 8), "Week of 2 March 2026"), (period.Start, period.End, period.Label));
    }

    [Fact]
    public void ThePreviousMonth_IsTheLastCalendarMonth_AcrossAYearEnd()
    {
        var period = ReportPeriods.Previous(ReportFrequency.Monthly, TimeZoneInfo.Utc, Utc(2026, 1, 15));

        Assert.Equal((new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31), "December 2025"), (period.Start, period.End, period.Label));
    }

    [Fact]
    public void ThePreviousQuarter_IsTheLastCalendarQuarter()
    {
        var period = ReportPeriods.Previous(ReportFrequency.Quarterly, TimeZoneInfo.Utc, Utc(2026, 5, 20));

        Assert.Equal((new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), "Q1 2026"), (period.Start, period.End, period.Label));
    }

    [Fact]
    public void ThePeriod_UsesTheLocalDate_NotTheUtcDate()
    {
        // 31 March 14:00 UTC is already 1 April in Sydney, so March is the previous month there.
        var period = ReportPeriods.Previous(ReportFrequency.Monthly, Sydney, Utc(2026, 3, 31, 14));

        Assert.Equal(new DateOnly(2026, 3, 1), period.Start);
    }

    [Fact]
    public void APeriod_StartsAndEndsAtLocalMidnight()
    {
        var march = new ReportPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), ReportPeriodKind.Month);

        // London is on GMT on 1 March and on BST (UTC+1) from 29 March.
        Assert.Equal((Utc(2026, 3, 1), Utc(2026, 3, 31, 23)), (ReportPeriods.StartUtc(march, London), ReportPeriods.EndUtc(march, London)));
    }

    [Fact]
    public void AReport_IsDueAtTheSendHourOnTheFirstLocalDayAfterThePeriod()
    {
        var march = new ReportPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), ReportPeriodKind.Month);

        Assert.Equal(Utc(2026, 4, 1, 5), ReportPeriods.DueUtc(march, London, sendHour: 6)); // 06:00 BST
        Assert.Equal(Utc(2026, 3, 31, 19), ReportPeriods.DueUtc(march, Sydney, sendHour: 6)); // 06:00 AEDT
    }

    [Fact]
    public void ASendHourThatDoesntExist_BecomesTheFirstValidTimeAfterIt()
    {
        // London's clocks go from 01:00 to 02:00 on 29 March 2026, so 01:00 that day doesn't exist.
        var week = new ReportPeriod(new DateOnly(2026, 3, 22), new DateOnly(2026, 3, 28), ReportPeriodKind.Week);

        Assert.Equal(Utc(2026, 3, 29, 1), ReportPeriods.DueUtc(week, London, sendHour: 1));
    }

    [Fact]
    public void AMonthsComparison_IsThePreviousMonth_AndACustomRangesIsTheEqualLengthBefore()
    {
        var march = new ReportPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), ReportPeriodKind.Month);
        var custom = new ReportPeriod(new DateOnly(2026, 3, 11), new DateOnly(2026, 3, 20), ReportPeriodKind.Custom);

        Assert.Equal(new ReportPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), ReportPeriodKind.Month), ReportPeriods.PreviousForComparison(march));
        Assert.Equal(new ReportPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 10), ReportPeriodKind.Custom), ReportPeriods.PreviousForComparison(custom));
    }

    [Theory]
    [InlineData("2026-03-02", "2026-03-08", ReportPeriodKind.Week, "Week of 2 March 2026")]
    [InlineData("2026-02-01", "2026-02-28", ReportPeriodKind.Month, "February 2026")]
    [InlineData("2026-01-01", "2026-03-31", ReportPeriodKind.Quarter, "Q1 2026")]
    [InlineData("2026-01-01", "2026-03-15", ReportPeriodKind.Custom, "1 January 2026 to 15 March 2026")]
    public void ARange_IsRecognisedAsTheKindOfPeriodItCovers(string start, string end, ReportPeriodKind kind, string label)
    {
        var period = ReportPeriods.FromRange(DateOnly.Parse(start), DateOnly.Parse(end), TimeZoneInfo.Utc, Utc(2026, 4, 2));

        Assert.Equal((kind, label), (period.Kind, period.Label));
    }

    [Theory]
    [InlineData("2026-03-10", "2026-03-01", "The start date must be on or before the end date.")]
    [InlineData("2026-03-01", "2026-04-02", "A report can only cover days that have ended.")]
    [InlineData("2025-01-01", "2026-03-01", "A report can cover at most 366 days.")]
    public void AnInvalidRange_IsRefused(string start, string end, string message)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            ReportPeriods.FromRange(DateOnly.Parse(start), DateOnly.Parse(end), TimeZoneInfo.Utc, Utc(2026, 4, 2, 12)));

        Assert.StartsWith(message, exception.Message);
    }

    [Fact]
    public void RecentPeriods_AreTheLastCompleteOnes_NewestFirst()
    {
        var months = ReportPeriods.Recent(ReportPeriodKind.Month, TimeZoneInfo.Utc, Utc(2026, 4, 2), count: 3);

        Assert.Equal(["March 2026", "February 2026", "January 2026"], months.Select(period => period.Label));
    }

    [Fact]
    public void AnUnknownTimeZone_FallsBackToUtc() =>
        Assert.Equal(TimeZoneInfo.Utc, ReportSettingsService.ResolveZone("Mars/Olympus_Mons"));
}
