using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public enum AcknowledgeOutcome
{
    Acknowledged,

    /// <summary>The alert is closed, but its Halo ticket couldn't be closed and needs closing by hand.</summary>
    AcknowledgedButTicketNotClosed,
    NotAcknowledgeable
}

/// <summary>Closes a DMARC policy weakened or nameservers changed alert, which may describe a deliberate change, and
/// accepts the current value. Check alerts close themselves when the check passes, so they can't be acknowledged.</summary>
public static class AlertAcknowledgement
{
    public static bool IsAcknowledgeable(string alertType) =>
        alertType is AlertTypes.DmarcPolicyWeakened or AlertTypes.NameserversChanged;

    public static async Task<AcknowledgeOutcome> AcknowledgeAsync(DotMarcDbContext context, AuditActor actor, int alertId, IPsaTicketService psaTicketService, CancellationToken cancellationToken = default)
    {
        var alert = await context.AlertEvents.SingleOrDefaultAsync(candidate => candidate.Id == alertId, cancellationToken).ConfigureAwait(false);
        if (alert is null || alert.IsResolved || !IsAcknowledgeable(alert.AlertType))
        {
            return AcknowledgeOutcome.NotAcknowledgeable;
        }

        // An alert left open past the cooldown is raised again as a new row, so close every open copy of it.
        var openCopies = await context.AlertEvents
            .Where(candidate => candidate.DomainName == alert.DomainName && candidate.AlertType == alert.AlertType && !candidate.IsResolved)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var resolvedUtc = DateTimeOffset.UtcNow;
        foreach (var copy in openCopies)
        {
            copy.IsResolved = true;
            copy.ResolvedUtc = resolvedUtc;
        }

        await DnsHealthBaselines.AcceptCurrentAsync(context, alert, cancellationToken).ConfigureAwait(false);
        AuditLog.Record(context, actor, AuditActions.AlertAcknowledged, AuditTarget.For(alert), $"Acknowledged \"{alert.Title}\" for {alert.DomainName}");
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var ticketsClosed = true;
        foreach (var copy in openCopies)
        {
            try
            {
                await psaTicketService.CloseTicketAsync(context, copy, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ticketsClosed = false;
            }
        }

        return ticketsClosed ? AcknowledgeOutcome.Acknowledged : AcknowledgeOutcome.AcknowledgedButTicketNotClosed;
    }
}
