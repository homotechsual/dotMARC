// src/DotMarc/Notifications/IPsaTicketService.cs
using DotMarc.Data;

namespace DotMarc.Notifications;

public interface IPsaTicketService
{
    Task CreateTicketAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken = default);
    Task CloseTicketAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken = default);
}
