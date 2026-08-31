using Microsoft.Extensions.Options;

namespace DotMarc.Notifications;

public interface IAlertWebhookClient
{
    Task SendAlertAsync(string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default);
}

public sealed class AlertWebhookClient : IAlertWebhookClient
{
    private readonly ITeamsWebhookClient _teamsWebhookClient;
    private readonly IGenericWebhookClient _genericWebhookClient;
    private readonly NotificationOptions _options;

    public AlertWebhookClient(ITeamsWebhookClient teamsWebhookClient, IGenericWebhookClient genericWebhookClient, IOptions<NotificationOptions> options)
    {
        _teamsWebhookClient = teamsWebhookClient;
        _genericWebhookClient = genericWebhookClient;
        _options = options.Value;
    }

    public async Task SendAlertAsync(string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var mode = _options.DeliveryMode ?? "Teams";
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
            await _teamsWebhookClient.SendAlertAsync(domainName, alertType, title, message, cancellationToken).ConfigureAwait(false);
        }

        if (useGeneric)
        {
            await _genericWebhookClient.SendAlertAsync(domainName, alertType, title, message, cancellationToken).ConfigureAwait(false);
        }
    }
}
