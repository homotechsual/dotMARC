// src/DotMarc/Notifications/PsaTicketService.cs
using DotMarc.Data;
using DotMarc.Psa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotMarc.Notifications;

/// <summary>Raises and closes an alert's tickets across every PSA. Each PSA is handled on its own, so one that is down
/// or misconfigured never stops the others.</summary>
public sealed class PsaTicketService(IEnumerable<IPsaProvider> providers, ILogger<PsaTicketService> logger) : IPsaTicketService
{
    public const string ResolvedNote = "Resolved automatically by dotMARC.";

    private readonly IReadOnlyList<IPsaProvider> _providers = providers.ToList();

    public async Task CreateTicketsAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken = default)
    {
        var domain = await PsaCompanyResolver.IncludeLinks(context.Domains)
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Name == alert.DomainName, cancellationToken)
            .ConfigureAwait(false);
        if (domain is null)
        {
            return;
        }

        foreach (var provider in _providers)
        {
            try
            {
                await CreateTicketAsync(context, provider, domain, alert, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Failed to raise a {Psa} ticket for {DomainName} alert {AlertType}.", provider.Kind.DisplayName(), alert.DomainName, alert.AlertType);
            }
        }
    }

    private static async Task CreateTicketAsync(DotMarcDbContext context, IPsaProvider provider, Domain domain, AlertEvent alert, CancellationToken cancellationToken)
    {
        if (!(await provider.GetReadinessAsync(context, cancellationToken).ConfigureAwait(false)).IsReady)
        {
            return;
        }

        if (PsaCompanyResolver.Resolve(domain, provider.Kind) is not { } company)
        {
            return;
        }

        // Only this alert type's rules matter, and of the group rules only the deciding group's for this PSA.
        var decidingGroupId = PsaCompanyResolver.ResolveGroup(domain, provider.Kind)?.Id;
        var rules = await context.AlertTicketRules
            .AsNoTracking()
            .Where(rule => rule.AlertType == alert.AlertType && (rule.GroupId == null || rule.GroupId == decidingGroupId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!AlertTicketPolicy.ShouldCreateTicket(alert.AlertType, domain, provider.Kind, rules))
        {
            return;
        }

        // AlertingService raises a new AlertEvent each cooldown window while a condition stays unhealthy, without
        // resolving the earlier one. If an earlier unresolved copy already has an open ticket in this PSA, don't raise
        // another there; the alert row itself is still recorded.
        var ticketAlreadyOpen = await context.AlertTickets.AnyAsync(ticket =>
                ticket.Psa == provider.Kind && ticket.IsOpen && ticket.AlertEventId != alert.Id
                && context.AlertEvents.Any(other => other.Id == ticket.AlertEventId && other.DomainName == alert.DomainName && other.AlertType == alert.AlertType && !other.IsResolved),
                cancellationToken)
            .ConfigureAwait(false);
        if (ticketAlreadyOpen)
        {
            return;
        }

        var ticketId = await provider.CreateTicketAsync(context, new PsaTicketRequest(company.CompanyId, alert.DomainName, alert.AlertType, alert.Title, alert.Message), cancellationToken).ConfigureAwait(false);
        context.AlertTickets.Add(new AlertTicket { AlertEventId = alert.Id, Psa = provider.Kind, TicketId = ticketId, IsOpen = true });
    }

    public async Task<PsaCloseResult> CloseTicketsAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken = default)
    {
        var tickets = await context.AlertTickets
            .Where(ticket => ticket.AlertEventId == alert.Id && ticket.IsOpen)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Filtered again in memory: a ticket this context has just marked closed (PsaTicketClosure does, before calling
        // this) is still open in the database, and the query returns the tracked instance.
        var result = PsaCloseResult.None;
        foreach (var ticket in tickets.Where(ticket => ticket.IsOpen))
        {
            result = result.Add(await CloseTicketAsync(context, ticket, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    /// <summary>Closes one ticket, marking it closed on success. Shared with the poller's retry.</summary>
    public async Task<PsaCloseResult> CloseTicketAsync(DotMarcDbContext context, AlertTicket ticket, CancellationToken cancellationToken = default)
    {
        var provider = _providers.FirstOrDefault(candidate => candidate.Kind == ticket.Psa);
        if (provider is null)
        {
            return new PsaCloseResult(0, 1);
        }

        try
        {
            await provider.CloseTicketAsync(context, ticket.TicketId, ResolvedNote, cancellationToken).ConfigureAwait(false);
            ticket.IsOpen = false;
            return new PsaCloseResult(1, 0);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to close {Psa} ticket {TicketId}. dotMARC will try again.", ticket.Psa.DisplayName(), ticket.TicketId);
            return new PsaCloseResult(0, 1);
        }
    }
}
