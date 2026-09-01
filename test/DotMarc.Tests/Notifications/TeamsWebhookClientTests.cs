using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class TeamsWebhookClientTests
{
    private static (TeamsWebhookClient client, FakeHttpMessageHandler handler) CreateClient()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler);
        return (new TeamsWebhookClient(http), handler);
    }

    [Fact]
    public async Task SendAlertAsync_PostsToTheConfiguredWebhookUrl()
    {
        var (client, handler) = CreateClient();
        var settings = new NotificationSettings { Enabled = true, TeamsWebhookUrl = "https://example.test/teams-webhook" };

        await client.SendAlertAsync(settings, "contoso.io", "MissedReport", "Missing report", "contoso.io has not sent a report.", CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal("https://example.test/teams-webhook", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task SendAlertAsync_DoesNothing_WhenDisabled()
    {
        var (client, handler) = CreateClient();
        var settings = new NotificationSettings { Enabled = false, TeamsWebhookUrl = "https://example.test/teams-webhook" };

        await client.SendAlertAsync(settings, "contoso.io", "MissedReport", "Missing report", "contoso.io has not sent a report.", CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SendAlertAsync_DoesNothing_WhenNoWebhookUrlIsConfigured()
    {
        var (client, handler) = CreateClient();
        var settings = new NotificationSettings { Enabled = true, TeamsWebhookUrl = null };

        await client.SendAlertAsync(settings, "contoso.io", "MissedReport", "Missing report", "contoso.io has not sent a report.", CancellationToken.None);

        Assert.Empty(handler.Requests);
    }
}
