using Microsoft.Extensions.Logging;

namespace DotMarc.Notifications;

public enum AlertChannel { Teams, Slack, GenericWebhook }

public interface IAlertWebhookClient
{
    Task SendAlertAsync(NotificationSettings settings, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default);

    /// <summary>Sends a clearly marked test alert to one channel's URL (saved or not), for the Send test button. Returns
    /// null when it was accepted, otherwise what went wrong.</summary>
    Task<string?> SendTestAsync(AlertChannel channel, string? webhookUrl, CancellationToken cancellationToken = default);
}

/// <summary>Sends an alert to every channel that's switched on and has a URL. Each channel is sent separately, so one
/// that's down or misconfigured doesn't stop the others; its failure is logged.</summary>
public sealed class AlertWebhookClient(
    ITeamsWebhookClient teamsWebhookClient, ISlackWebhookClient slackWebhookClient, IGenericWebhookClient genericWebhookClient,
    ILogger<AlertWebhookClient> logger) : IAlertWebhookClient
{
    public async Task SendAlertAsync(NotificationSettings settings, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled)
        {
            return;
        }

        foreach (var (channel, enabled, webhookUrl) in Channels(settings))
        {
            if (!enabled || string.IsNullOrWhiteSpace(webhookUrl))
            {
                continue;
            }

            try
            {
                await SendAsync(channel, webhookUrl, domainName, alertType, title, message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Failed to send the {AlertType} alert for {DomainName} to {Channel}.", alertType, domainName, channel);
            }
        }
    }

    public async Task<string?> SendTestAsync(AlertChannel channel, string? webhookUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return "Enter the webhook URL first.";
        }

        if (WebhookUrls.Problem(webhookUrl, "The webhook URL") is { } problem)
        {
            return problem;
        }

        try
        {
            await SendAsync(channel, webhookUrl, "example.com", "Test", "Test alert from dotMARC",
                "This is a test from dotMARC's alert settings. If you can see it, alerts will reach this channel.", cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return exception.Message;
        }
    }

    private Task SendAsync(AlertChannel channel, string webhookUrl, string domainName, string alertType, string title, string message, CancellationToken cancellationToken) => channel switch
    {
        AlertChannel.Teams => teamsWebhookClient.SendAlertAsync(webhookUrl, domainName, alertType, title, message, cancellationToken),
        AlertChannel.Slack => slackWebhookClient.SendAlertAsync(webhookUrl, domainName, alertType, title, message, cancellationToken),
        _ => genericWebhookClient.SendAlertAsync(webhookUrl, domainName, alertType, title, message, cancellationToken),
    };

    private static IEnumerable<(AlertChannel Channel, bool Enabled, string? WebhookUrl)> Channels(NotificationSettings settings) =>
    [
        (AlertChannel.Teams, settings.TeamsEnabled, settings.TeamsWebhookUrl),
        (AlertChannel.Slack, settings.SlackEnabled, settings.SlackWebhookUrl),
        (AlertChannel.GenericWebhook, settings.GenericWebhookEnabled, settings.GenericWebhookUrl),
    ];
}
