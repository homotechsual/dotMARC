using System.Globalization;
using PdfSharp.Drawing;

namespace DotMarc.Reporting.ClientReports;

public sealed record TrendSeries(string Name, string ColourHex, IReadOnlyList<double?> Points);

/// <summary>The report's pass rate trend, drawn straight onto the PDF page: a line per domain, broken where a day had no
/// mail, over a scale that starts just below the lowest rate (pass rates cluster near 100%, so a 0 to 100 scale would
/// flatten every line). Drawn by hand because MigraDoc's charts render blanks as zero and mislay their axis labels.</summary>
public static class TrendChart
{
    private const double LegendHeight = 16;
    private const double AxisLabelWidth = 30;
    private const double XLabelHeight = 14;
    private const int MaximumGridSteps = 5;
    private static readonly double[] GridStepSizes = [1, 2, 5, 10, 20, 25];

    /// <summary>Inclusive index ranges of consecutive days with data, each drawn as one line.</summary>
    public static IReadOnlyList<(int Start, int End)> Runs(IReadOnlyList<double?> points)
    {
        var runs = new List<(int Start, int End)>();
        int? start = null;
        for (var index = 0; index <= points.Count; index++)
        {
            var hasValue = index < points.Count && points[index] is not null;
            if (hasValue && start is null)
            {
                start = index;
            }
            else if (!hasValue && start is { } runStart)
            {
                runs.Add((runStart, index - 1));
                start = null;
            }
        }

        return runs;
    }

    /// <summary>The percentage range shown: from a multiple of 5 at least 5 below the lowest rate, to 100.</summary>
    public static (double Low, double High) Scale(IEnumerable<TrendSeries> series)
    {
        var rates = series.SelectMany(line => line.Points).OfType<double>().ToList();
        if (rates.Count == 0)
        {
            return (0, 100);
        }

        var low = (Math.Floor(rates.Min() * 100 / 5) * 5) - 5;
        return (Math.Max(0, low), 100);
    }

    /// <summary>Gridline values from low to high on round numbers: the smallest step from 1, 2, 5, 10, 20 or 25 that
    /// needs at most five steps.</summary>
    public static IReadOnlyList<double> Gridlines(double low, double high)
    {
        var step = GridStepSizes.FirstOrDefault(size => (high - low) / size <= MaximumGridSteps, high - low);
        var lines = new List<double>();
        for (var value = high; value >= low - 0.0001; value -= step)
        {
            lines.Add(value);
        }

        lines.Reverse();
        return lines;
    }

    /// <summary>Where each legend entry goes: left to right, starting a new row when the next one wouldn't fit.</summary>
    public static IReadOnlyList<(int Row, double X)> LegendLayout(IReadOnlyList<double> entryWidths, double availableWidth)
    {
        var positions = new List<(int Row, double X)>();
        var (row, x) = (0, 0.0);
        foreach (var width in entryWidths)
        {
            if (x > 0 && x + width > availableWidth)
            {
                (row, x) = (row + 1, 0.0);
            }

            positions.Add((row, x));
            x += width;
        }

        return positions;
    }

    public static void Paint(XGraphics graphics, XRect area, IReadOnlyList<TrendSeries> series, IReadOnlyList<string> xLabels)
    {
        var labelFont = new XFont(ReportFontResolver.FamilyName, 7);
        var legendFont = new XFont(ReportFontResolver.FamilyName, 8);
        var grey = XColor.FromArgb(0x9E, 0x9E, 0x9E);

        // Legend across the top, wrapping onto more rows when the names don't fit on one.
        var legendLeft = area.Left + AxisLabelWidth;
        var entryWidths = series.Select(line => 18 + graphics.MeasureString(line.Name, legendFont).Width + 14).ToList();
        var legend = LegendLayout(entryWidths, area.Right - legendLeft);
        for (var index = 0; index < series.Count; index++)
        {
            var (row, offset) = legend[index];
            var x = legendLeft + offset;
            var y = area.Top + row * LegendHeight;
            graphics.DrawLine(new XPen(Colour(series[index].ColourHex), 2), x, y + 6, x + 14, y + 6);
            graphics.DrawString(series[index].Name, legendFont, XBrushes.Black, x + 18, y + 9);
        }

        var legendHeight = (legend.Count == 0 ? 1 : legend.Max(entry => entry.Row) + 1) * LegendHeight;
        var plot = new XRect(area.Left + AxisLabelWidth, area.Top + legendHeight, area.Width - AxisLabelWidth, area.Height - legendHeight - XLabelHeight);
        var (low, high) = Scale(series);
        double Y(double rate) => plot.Bottom - ((rate * 100) - low) / (high - low) * plot.Height;
        var count = Math.Max(1, series.Count == 0 ? 0 : series.Max(line => line.Points.Count));
        double X(int index) => count == 1 ? plot.Left + plot.Width / 2 : plot.Left + index * plot.Width / (count - 1);

        // Gridlines and percentage labels.
        var gridPen = new XPen(XColor.FromArgb(0xE0, 0xE0, 0xE0), 0.5);
        foreach (var value in Gridlines(low, high))
        {
            var y = Y(value / 100);
            graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            var label = string.Create(CultureInfo.InvariantCulture, $"{value:0}%");
            var width = graphics.MeasureString(label, labelFont).Width;
            graphics.DrawString(label, labelFont, new XSolidBrush(grey), plot.Left - width - 4, y + 2.5);
        }

        // Day labels along the bottom.
        for (var index = 0; index < xLabels.Count && index < count; index++)
        {
            if (xLabels[index].Length == 0)
            {
                continue;
            }

            var width = graphics.MeasureString(xLabels[index], labelFont).Width;
            graphics.DrawString(xLabels[index], labelFont, new XSolidBrush(grey), X(index) - width / 2, plot.Bottom + 10);
        }

        // A line per domain, broken where there was no data; a lone day is a dot.
        foreach (var line in series)
        {
            var colour = Colour(line.ColourHex);
            var pen = new XPen(colour, 1.5) { LineJoin = XLineJoin.Round };
            foreach (var (start, end) in Runs(line.Points))
            {
                if (start == end)
                {
                    graphics.DrawEllipse(new XSolidBrush(colour), X(start) - 1.5, Y(line.Points[start]!.Value) - 1.5, 3, 3);
                    continue;
                }

                var points = Enumerable.Range(start, end - start + 1).Select(index => new XPoint(X(index), Y(line.Points[index]!.Value))).ToArray();
                graphics.DrawLines(pen, points);
            }
        }
    }

    private static XColor Colour(string hex) => XColor.FromArgb(
        int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}
