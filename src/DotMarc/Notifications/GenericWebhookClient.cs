using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace DotMarc.Notifications;

public interface IGenericWebhookClient
{
    Task SendAlertAsync(string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default);
}

public sealed class GenericWebhookClient : IGenericWebhookClient
{
    private readonly HttpClient _httpClient;
    private readonly NotificationOptions _options;

    public GenericWebhookClient(HttpClient httpClient, IOptions<NotificationOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task SendAlertAsync(string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.GenericWebhookUrl))
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

        using var response = await _httpClient.PostAsJsonAsync(_options.GenericWebhookUrl, payload, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
