using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class AlertWebhookClientTests
{
    [Theory]
    [InlineData("Teams", true, false)]
    [InlineData("Generic", false, true)]
    [InlineData("Both", true, true)]
    [InlineData("SomethingUnrecognized", true, false)] // falls back to Teams, matching the pre-existing default
    public async Task SendAlertAsync_RoutesToTheClientsImpliedByDeliveryMode(string deliveryMode, bool expectTeams, bool expectGeneric)
    {
        var teams = new FakeTeamsWebhookClient();
        var generic = new FakeGenericWebhookClient();
        var client = new AlertWebhookClient(teams, generic);
        var settings = new NotificationSettings { Enabled = true, DeliveryMode = deliveryMode };

        await client.SendAlertAsync(settings, "contoso.io", "MissedReport", "Missing report", "contoso.io has not sent a report.", CancellationToken.None);

        Assert.Equal(expectTeams ? 1 : 0, teams.CallCount);
        Assert.Equal(expectGeneric ? 1 : 0, generic.CallCount);
    }

    [Fact]
    public async Task SendAlertAsync_CallsNeitherClient_WhenDisabled()
    {
        var teams = new FakeTeamsWebhookClient();
        var generic = new FakeGenericWebhookClient();
        var client = new AlertWebhookClient(teams, generic);
        var settings = new NotificationSettings { Enabled = false, DeliveryMode = "Both" };

        await client.SendAlertAsync(settings, "contoso.io", "MissedReport", "Missing report", "contoso.io has not sent a report.", CancellationToken.None);

        Assert.Equal(0, teams.CallCount);
        Assert.Equal(0, generic.CallCount);
    }

    private sealed class FakeTeamsWebhookClient : ITeamsWebhookClient
    {
        public int CallCount { get; private set; }

        public Task SendAlertAsync(NotificationSettings settings, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGenericWebhookClient : IGenericWebhookClient
    {
        public int CallCount { get; private set; }

        public Task SendAlertAsync(NotificationSettings settings, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }
}
