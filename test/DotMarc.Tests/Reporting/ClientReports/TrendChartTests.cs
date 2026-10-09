using DotMarc.Reporting.ClientReports;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

public sealed class TrendChartTests
{
    [Fact]
    public void DaysWithoutData_BreakTheLineIntoRuns()
    {
        Assert.Equal([(3, 4), (6, 9)], TrendChart.Runs([null, null, null, 0.9, 0.95, null, 1.0, 0.98, 0.99, 1.0]));
    }

    [Fact]
    public void ALoneDay_IsARunOfOne() => Assert.Equal([(1, 1)], TrendChart.Runs([null, 0.5, null]));

    [Fact]
    public void NoData_HasNoRuns() => Assert.Empty(TrendChart.Runs([null, null]));

    [Fact]
    public void TheScale_StartsJustBelowTheLowestRate_AndEndsAt100()
    {
        var scale = TrendChart.Scale([new TrendSeries("aurora-retail.example", "#0B5FFF", [0.97, null, 0.995])]);

        Assert.Equal((90.0, 100.0), scale);
    }

    [Theory]
    [InlineData(90, 100, new double[] { 90, 92, 94, 96, 98, 100 })]
    [InlineData(0, 100, new double[] { 0, 20, 40, 60, 80, 100 })]
    [InlineData(75, 100, new double[] { 75, 80, 85, 90, 95, 100 })]
    public void Gridlines_FallOnRoundNumbers(double low, double high, double[] expected) =>
        Assert.Equal(expected, TrendChart.Gridlines(low, high));

    [Fact]
    public void TheScale_NeverGoesBelowZero_AndIsWholeWithNoData()
    {
        Assert.Equal((0.0, 100.0), TrendChart.Scale([new TrendSeries("a", "#000000", [0.02])]));
        Assert.Equal((0.0, 100.0), TrendChart.Scale([new TrendSeries("a", "#000000", [null])]));
    }
}
