using System.Net.Http.Json;

namespace DotMarc.Notifications;

public interface IGenericWebhookClient
{
    Task SendAlertAsync(NotificationSettings settings, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default);
}

public sealed class GenericWebhookClient : IGenericWebhookClient
{
    private readonly HttpClient _httpClient;

    public GenericWebhookClient(HttpClient httpClient) => _httpClient = httpClient;

    public async Task SendAlertAsync(NotificationSettings settings, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.GenericWebhookUrl))
        {
            return;
        }

        var payload = new
        {
            domainName,
            alertType,
            title,
            message,
            severity = "Warning",
            createdUtc = DateTimeOffset.UtcNow
        };

        using var response = await _httpClient.PostAsJsonAsync(settings.GenericWebhookUrl, payload, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
