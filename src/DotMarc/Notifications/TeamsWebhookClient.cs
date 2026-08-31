using System.Net.Http.Json;

namespace DotMarc.Notifications;

public interface ITeamsWebhookClient
{
    Task SendAlertAsync(NotificationSettings settings, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default);
}

public sealed class TeamsWebhookClient : ITeamsWebhookClient
{
    private readonly HttpClient _httpClient;

    public TeamsWebhookClient(HttpClient httpClient) => _httpClient = httpClient;

    public async Task SendAlertAsync(NotificationSettings settings, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.TeamsWebhookUrl))
        {
            return;
        }

        var payload = BuildPayload(domainName, alertType, title, message);
        using var response = await _httpClient.PostAsJsonAsync(settings.TeamsWebhookUrl, payload, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    internal static object BuildPayload(string domainName, string alertType, string title, string message)
    {
        return new Dictionary<string, object?>
        {
            ["type"] = "message",
            ["attachments"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["contentType"] = "application/vnd.microsoft.card.adaptive",
                    ["contentUrl"] = null,
                    ["content"] = new Dictionary<string, object?>
                    {
                        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                        ["type"] = "AdaptiveCard",
                        ["version"] = "1.5",
                        ["body"] = new object[]
                        {
                            new Dictionary<string, object?> { ["type"] = "TextBlock", ["text"] = "dotMARC alert", ["weight"] = "Bolder", ["size"] = "Medium" },
                            new Dictionary<string, object?> { ["type"] = "TextBlock", ["text"] = title, ["wrap"] = true, ["weight"] = "Bolder" },
                            new Dictionary<string, object?> { ["type"] = "FactSet", ["facts"] = new object[] {
                                new Dictionary<string, object?> { ["title"] = "Domain", ["value"] = domainName },
                                new Dictionary<string, object?> { ["title"] = "Alert", ["value"] = alertType },
                                new Dictionary<string, object?> { ["title"] = "Message", ["value"] = message }
                            } }
                        },
                        ["msteams"] = new Dictionary<string, object?> { ["width"] = "Full" }
                    }
                }
            }
        };
    }
}
