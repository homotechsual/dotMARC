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

        // AlertingService's cooldown logic (pre-dating this feature) creates a new AlertEvent once
        // the cooldown elapses for a still-unresolved condition, without resolving the earlier one
        // — a domain that stays unhealthy would otherwise accumulate a fresh open Halo ticket every
        // cooldown window indefinitely. If an earlier unresolved alert for this same domain+type
        // already has an open ticket, skip creating another one for this occurrence; the AlertEvent
        // row itself is still recorded as normal, only the ticket is deduplicated.
        var ticketAlreadyOpen = await context.AlertEvents.AnyAsync(e =>
                e.DomainName == alert.DomainName && e.AlertType == alert.AlertType && e.Id != alert.Id && !e.IsResolved && e.ExternalTicketId != null,
                cancellationToken)
            .ConfigureAwait(false);
        if (ticketAlreadyOpen)
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
