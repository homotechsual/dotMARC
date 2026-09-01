namespace DotMarc.Notifications;

public interface IAlertWebhookClient
{
    Task SendAlertAsync(NotificationSettings settings, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default);
}

public sealed class AlertWebhookClient : IAlertWebhookClient
{
    private readonly ITeamsWebhookClient _teamsWebhookClient;
    private readonly IGenericWebhookClient _genericWebhookClient;

    public AlertWebhookClient(ITeamsWebhookClient teamsWebhookClient, IGenericWebhookClient genericWebhookClient)
    {
        _teamsWebhookClient = teamsWebhookClient;
        _genericWebhookClient = genericWebhookClient;
    }

    public async Task SendAlertAsync(NotificationSettings settings, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled)
        {
            return;
        }

        var mode = settings.DeliveryMode ?? "Teams";
        var useTeams = string.Equals(mode, "Teams", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "Both", StringComparison.OrdinalIgnoreCase);
        var useGeneric = string.Equals(mode, "Generic", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "Both", StringComparison.OrdinalIgnoreCase);

        if (!useTeams && !useGeneric)
        {
            useTeams = true;
        }

        if (useTeams)
        {
            await _teamsWebhookClient.SendAlertAsync(settings, domainName, alertType, title, message, cancellationToken).ConfigureAwait(false);
        }

        if (useGeneric)
        {
            await _genericWebhookClient.SendAlertAsync(settings, domainName, alertType, title, message, cancellationToken).ConfigureAwait(false);
        }
    }
}
