// src/DotMarc/Notifications/PsaTicketService.cs
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public sealed class PsaTicketService : IPsaTicketService
{
    private const string ProviderName = "HaloPSA";
    private readonly IHaloPsaClient _haloPsaClient;

    public PsaTicketService(IHaloPsaClient haloPsaClient) => _haloPsaClient = haloPsaClient;

    public async Task CreateTicketAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken = default)
    {
        var settings = await HaloPsaSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        var domain = await context.Domains
            .Include(d => d.Groups)
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.Name == alert.DomainName, cancellationToken)
            .ConfigureAwait(false);
        if (domain is null)
        {
            return;
        }

        var haloClientId = HaloClientResolver.Resolve(domain);
        if (haloClientId is null)
        {
            return;
        }

        var ticketId = await _haloPsaClient.CreateTicketAsync(settings, haloClientId.Value, alert.DomainName, alert.AlertType, alert.Title, alert.Message, cancellationToken).ConfigureAwait(false);
        alert.ExternalTicketProvider = ProviderName;
        alert.ExternalTicketId = ticketId;
    }

    public async Task CloseTicketAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken = default)
    {
        if (alert.ExternalTicketProvider != ProviderName || alert.ExternalTicketId is null)
        {
            return;
        }

        var settings = await HaloPsaSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        await _haloPsaClient.CloseTicketAsync(settings, alert.ExternalTicketId, "Resolved automatically by dotMARC.", cancellationToken).ConfigureAwait(false);
    }
}
