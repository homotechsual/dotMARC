// test/DotMarc.Tests/Demo/DemoDataResetServiceTests.cs
using DotMarc.Demo;
using Xunit;

namespace DotMarc.Tests.Demo;

public sealed class DemoDataResetServiceTests
{
    [Fact]
    public void GetDelayUntilNextReset_ReturnsTimeUntilTodaysResetHour_WhenBeforeIt()
    {
        var now = new DateTimeOffset(2026, 8, 28, 1, 0, 0, TimeSpan.Zero);

        var delay = DemoDataResetService.GetDelayUntilNextReset(now, resetHourUtc: 4);

        Assert.Equal(TimeSpan.FromHours(3), delay);
    }

    [Fact]
    public void GetDelayUntilNextReset_RollsOverToTomorrow_WhenAfterTodaysResetHour()
    {
        var now = new DateTimeOffset(2026, 8, 28, 10, 0, 0, TimeSpan.Zero);

        var delay = DemoDataResetService.GetDelayUntilNextReset(now, resetHourUtc: 4);

        Assert.Equal(TimeSpan.FromHours(18), delay);
    }

    [Fact]
    public void SeedFor_IsStableWithinTheSameUtcDay_ButDiffersAcrossDays()
    {
        var morning = new DateTimeOffset(2026, 8, 28, 1, 0, 0, TimeSpan.Zero);
        var evening = new DateTimeOffset(2026, 8, 28, 23, 0, 0, TimeSpan.Zero);
        var nextDay = new DateTimeOffset(2026, 8, 29, 1, 0, 0, TimeSpan.Zero);

        Assert.Equal(DemoDataResetService.SeedFor(morning), DemoDataResetService.SeedFor(evening));
        Assert.NotEqual(DemoDataResetService.SeedFor(morning), DemoDataResetService.SeedFor(nextDay));
    }
}
