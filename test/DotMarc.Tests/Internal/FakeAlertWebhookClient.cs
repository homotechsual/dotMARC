using DotMarc.Notifications;

namespace DotMarc.Tests.Internal;

internal sealed class FakeAlertWebhookClient : IAlertWebhookClient
{
    public int CallCount { get; private set; }
    public List<(string DomainName, string AlertType, string Severity)> Sent { get; } = [];

    /// <summary>The names of the destinations each call was given, in call order.</summary>
    public List<string[]> Destinations { get; } = [];

    /// <summary>The title of each call, in call order.</summary>
    public List<string> Titles { get; } = [];

    public Task SendAlertAsync(IReadOnlyList<AlertDestination> destinations, string domainName, string alertType, string severity, string title, string message, CancellationToken cancellationToken = default)
    {
        CallCount++;
        Sent.Add((domainName, alertType, severity));
        Destinations.Add([.. destinations.Select(destination => destination.Name)]);
        Titles.Add(title);
        return Task.CompletedTask;
    }

    public Task<string?> SendTestAsync(AlertChannel channel, string? webhookUrl, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
