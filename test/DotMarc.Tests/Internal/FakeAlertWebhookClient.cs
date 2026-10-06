using DotMarc.Notifications;

namespace DotMarc.Tests.Internal;

internal sealed class FakeAlertWebhookClient : IAlertWebhookClient
{
    public int CallCount { get; private set; }
    public List<(string DomainName, string AlertType)> Sent { get; } = [];

    public Task SendAlertAsync(NotificationSettings settings, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default)
    {
        CallCount++;
        Sent.Add((domainName, alertType));
        return Task.CompletedTask;
    }

    public Task<string?> SendTestAsync(AlertChannel channel, string? webhookUrl, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
