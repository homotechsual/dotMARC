using System.Net;
using System.Text.Json;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class SlackWebhookClientTests
{
    private static (SlackWebhookClient client, FakeHttpMessageHandler handler) CreateClient()
    {
        var handler = new FakeHttpMessageHandler();
        return (new SlackWebhookClient(new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task SendAlertAsync_PostsABlockKitMessage_ToTheWebhookUrl()
    {
        var (client, handler) = CreateClient();

        await client.SendAlertAsync("https://hooks.slack.com/services/T000/B000/secret", "contoso.io", "MissedReport", "Missing DMARC report",
            "contoso.io has not sent a report.", CancellationToken.None);

        Assert.Equal("https://hooks.slack.com/services/T000/B000/secret", handler.Requests.Single().RequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.RequestBodies.Single());
        Assert.Contains("Missing DMARC report", body.RootElement.GetProperty("text").GetString());
        Assert.Contains("contoso.io", body.RootElement.GetProperty("text").GetString());
        var blocks = body.RootElement.GetProperty("blocks").EnumerateArray().ToList();
        Assert.Equal("header", blocks[0].GetProperty("type").GetString());
        Assert.Equal("dotMARC alert", blocks[0].GetProperty("text").GetProperty("text").GetString());
        Assert.Contains(blocks, block => block.GetProperty("type").GetString() == "section"
            && block.TryGetProperty("fields", out var fields)
            && fields.EnumerateArray().Any(field => field.GetProperty("text").GetString()!.Contains("contoso.io"))
            && fields.EnumerateArray().Any(field => field.GetProperty("text").GetString()!.Contains("MissedReport")));
        Assert.Contains(blocks, block => block.GetProperty("type").GetString() == "section"
            && block.TryGetProperty("text", out var text) && text.GetProperty("text").GetString()!.Contains("contoso.io has not sent a report."));
    }

    [Fact]
    public async Task SendAlertAsync_Throws_WhenSlackRefusesTheMessage()
    {
        var (client, handler) = CreateClient();
        handler.StatusCode = HttpStatusCode.NotFound;

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAlertAsync("https://hooks.slack.com/services/gone", "contoso.io", "MissedReport",
            "Missing DMARC report", "contoso.io has not sent a report.", CancellationToken.None));
    }
}
