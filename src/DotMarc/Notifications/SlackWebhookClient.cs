using System.Net.Http.Json;

namespace DotMarc.Notifications;

public interface ISlackWebhookClient
{
    Task SendAlertAsync(string webhookUrl, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default);
}

/// <summary>Posts an alert to a Slack incoming webhook, laid out like the Teams card: a header, the title, the domain and
/// alert type side by side, then the message. The top-level text is what Slack shows in notifications and in clients
/// that can't render blocks.</summary>
public sealed class SlackWebhookClient(HttpClient httpClient) : ISlackWebhookClient
{
    public async Task SendAlertAsync(string webhookUrl, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(webhookUrl, BuildPayload(domainName, alertType, title, message), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    internal static object BuildPayload(string domainName, string alertType, string title, string message) => new
    {
        text = $"dotMARC alert: {title} ({domainName})",
        blocks = new object[]
        {
            new { type = "header", text = new { type = "plain_text", text = "dotMARC alert" } },
            new { type = "section", text = new { type = "mrkdwn", text = $"*{Escape(title)}*" } },
            new
            {
                type = "section",
                fields = new object[]
                {
                    new { type = "mrkdwn", text = $"*Domain*\n{Escape(domainName)}" },
                    new { type = "mrkdwn", text = $"*Alert*\n{Escape(alertType)}" },
                },
            },
            new { type = "section", text = new { type = "mrkdwn", text = Escape(message) } },
        },
    };

    /// <summary>Slack's mrkdwn treats &amp;, &lt; and &gt; as control characters; these are the escapes it documents.</summary>
    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
