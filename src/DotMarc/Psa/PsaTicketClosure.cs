using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Psa;

/// <summary>What happens when a tech closes an alert's ticket in a PSA, however dotMARC hears about it (the Halo
/// webhook or the poller): the alert resolves, a policy or nameserver change is accepted as Acknowledge does, the
/// alert's other open tickets are closed, and the audit log says which PSA closed it.</summary>
public static class PsaTicketClosure
{
    public static readonly AuditActor Actor = AuditActor.ForSystem("PSA ticket sync");

    /// <summary>True when an unresolved alert was resolved. The caller saves.</summary>
    public static async Task<bool> ResolveFromTicketAsync(DotMarcDbContext context, IPsaTicketService ticketService, PsaKind psa, string ticketId, CancellationToken cancellationToken)
    {
        var ticket = await context.AlertTickets.FirstOrDefaultAsync(candidate => candidate.Psa == psa && candidate.TicketId == ticketId && candidate.IsOpen, cancellationToken).ConfigureAwait(false);
        if (ticket is null)
        {
            return false;
        }

        ticket.IsOpen = false;
        var alert = await context.AlertEvents.SingleAsync(candidate => candidate.Id == ticket.AlertEventId, cancellationToken).ConfigureAwait(false);
        if (alert.IsResolved)
        {
            return false;
        }

        alert.IsResolved = true;
        alert.ResolvedUtc = DateTimeOffset.UtcNow;
        await DnsHealthBaselines.AcceptCurrentAsync(context, alert, cancellationToken).ConfigureAwait(false);
        AuditLog.Record(context, Actor, AuditActions.AlertResolvedByTicket, AuditTarget.For(alert), $"Resolved \"{alert.Title}\" for {alert.DomainName}: ticket {ticketId} was closed in {psa.DisplayName()}");
        await ticketService.CloseTicketsAsync(context, alert, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
