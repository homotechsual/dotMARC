// src/DotMarc/Notifications/IPsaTicketService.cs
using DotMarc.Data;
using DotMarc.Psa;

namespace DotMarc.Notifications;

public interface IPsaTicketService
{
    /// <summary>Raises a ticket in each ready PSA the alert's domain maps to, recording each as an AlertTicket. The
    /// caller saves.</summary>
    Task CreateTicketsAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken = default);

    /// <summary>Closes each of the alert's open tickets. A ticket that fails stays open, so the poller retries it.
    /// The caller saves.</summary>
    Task<PsaCloseResult> CloseTicketsAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken = default);
}
