using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Psa;

/// <summary>Which tickets to show against each alert. An alert left open past the cooldown is raised again as a new row, and
/// that row is deliberately not given a second ticket while an earlier copy's is open (see PsaTicketService). The ticket still
/// belongs to the condition, so every open copy shows it, and the newest rows, which are the ones people read first, are not
/// shown as having none.</summary>
public static class AlertTicketLinks
{
    /// <summary>Each alert's own tickets, plus the open tickets of other open copies of the same alert (same domain and type) when
    /// the alert is itself open. A resolved alert shows only its own, so history reads as it happened.</summary>
    public static async Task<ILookup<int, AlertTicket>> LoadAsync(DotMarcDbContext context, IReadOnlyList<AlertEvent> alerts, CancellationToken cancellationToken = default)
    {
        if (alerts.Count == 0)
        {
            return Array.Empty<AlertTicket>().ToLookup(ticket => ticket.AlertEventId);
        }

        var alertIds = alerts.Select(alert => alert.Id).ToList();
        var own = await context.AlertTickets.AsNoTracking()
            .Where(ticket => alertIds.Contains(ticket.AlertEventId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var openAlerts = alerts.Where(alert => !alert.IsResolved).ToList();
        var shared = new List<(string DomainName, string AlertType, AlertTicket Ticket)>();
        if (openAlerts.Count > 0)
        {
            var domainNames = openAlerts.Select(alert => alert.DomainName).Distinct().ToList();
            var alertTypes = openAlerts.Select(alert => alert.AlertType).Distinct().ToList();
            var siblings = await (
                    from ticket in context.AlertTickets.AsNoTracking()
                    join other in context.AlertEvents.AsNoTracking() on ticket.AlertEventId equals other.Id
                    where ticket.IsOpen && !other.IsResolved && domainNames.Contains(other.DomainName) && alertTypes.Contains(other.AlertType)
                    select new { Ticket = ticket, other.DomainName, other.AlertType })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            shared = [.. siblings.Select(sibling => (sibling.DomainName, sibling.AlertType, sibling.Ticket))];
        }

        var result = new List<(int AlertId, AlertTicket Ticket)>();
        foreach (var alert in alerts)
        {
            var tickets = own.Where(ticket => ticket.AlertEventId == alert.Id).ToList();
            if (!alert.IsResolved)
            {
                tickets.AddRange(shared
                    .Where(candidate => candidate.DomainName == alert.DomainName && candidate.AlertType == alert.AlertType)
                    .Select(candidate => candidate.Ticket)
                    .Where(candidate => tickets.All(existing => existing.Id != candidate.Id)));
            }

            result.AddRange(tickets.OrderBy(ticket => ticket.CreatedUtc).ThenBy(ticket => ticket.Id).Select(ticket => (alert.Id, ticket)));
        }

        return result.ToLookup(entry => entry.AlertId, entry => entry.Ticket);
    }
}
