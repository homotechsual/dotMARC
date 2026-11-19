using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class AlertDestinationRoutingTests
{
    private static AlertDestination Destination(int id, string name, bool enabled = true, string url = "https://example.test/hook") =>
        new() { Id = id, Name = name, Kind = AlertChannel.Teams, Url = url, Enabled = enabled };

    private static AlertRule Rule(AlertDestinationMode mode, params int[] destinationIds) => new()
    {
        Kind = AlertTypes.MissedReport, Name = "Rule", Severity = "Warning", DestinationMode = mode,
        Destinations = [.. destinationIds.Select(id => new AlertRuleDestination { AlertDestinationId = id })]
    };

    private static string[] Names(IReadOnlyList<AlertDestination> destinations) => [.. destinations.Select(destination => destination.Name)];

    [Fact]
    public void NoRule_SendsToEveryEnabledDestination()
    {
        var all = new[] { Destination(1, "A"), Destination(2, "B", enabled: false), Destination(3, "C") };

        Assert.Equal(["A", "C"], Names(AlertDestinationRouting.Resolve(null, all)));
    }

    [Fact]
    public void All_SkipsDisabledAndBlankUrlDestinations()
    {
        var all = new[] { Destination(1, "A"), Destination(2, "B", enabled: false), Destination(3, "C", url: " ") };

        Assert.Equal(["A"], Names(AlertDestinationRouting.Resolve(Rule(AlertDestinationMode.All), all)));
    }

    [Fact]
    public void All_IgnoresALeftOverSelection()
    {
        var all = new[] { Destination(1, "A"), Destination(2, "B") };

        Assert.Equal(["A", "B"], Names(AlertDestinationRouting.Resolve(Rule(AlertDestinationMode.All, 1), all)));
    }

    [Fact]
    public void Selected_SendsOnlyToTheChosenDestinations()
    {
        var all = new[] { Destination(1, "A"), Destination(2, "B"), Destination(3, "C") };

        Assert.Equal(["A", "C"], Names(AlertDestinationRouting.Resolve(Rule(AlertDestinationMode.Selected, 1, 3), all)));
    }

    [Fact]
    public void Selected_DoesNotFallBackToTheOthers_WhenAChosenDestinationIsDisabled()
    {
        var all = new[] { Destination(1, "A"), Destination(2, "B", enabled: false) };

        Assert.Empty(AlertDestinationRouting.Resolve(Rule(AlertDestinationMode.Selected, 2), all));
    }

    [Fact]
    public void Selected_WithNoneChosen_SendsNowhere()
    {
        var all = new[] { Destination(1, "A") };

        Assert.Empty(AlertDestinationRouting.Resolve(Rule(AlertDestinationMode.Selected), all));
    }
}
