using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class GenericWebhookClientTests
{
    private static (GenericWebhookClient client, FakeHttpMessageHandler handler) CreateClient()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler);
        return (new GenericWebhookClient(http), handler);
    }

    [Fact]
    public async Task SendAlertAsync_PostsToTheConfiguredWebhookUrl()
    {
        var (client, handler) = CreateClient();
        await client.SendAlertAsync("https://example.test/generic-webhook", "contoso.io", "MissedReport", "Missing report", "contoso.io has not sent a report.", CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal("https://example.test/generic-webhook", handler.Requests[0].RequestUri!.ToString());
    }
}
