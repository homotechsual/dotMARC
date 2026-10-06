using DotMarc.Data;

namespace DotMarc.Psa;

/// <summary>One PSA, as the shared ticket code sees it. Each implementation reads its own settings row from the
/// context it's given. Pick-lists for the settings page (boards, queues, statuses) stay on each PSA's own client,
/// because they differ per PSA and only its settings tab uses them.</summary>
public interface IPsaProvider
{
    PsaKind Kind { get; }
    Task<PsaReadiness> GetReadinessAsync(DotMarcDbContext context, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(DotMarcDbContext context, CancellationToken cancellationToken = default);

    /// <summary>Raises the ticket and returns its id in the PSA.</summary>
    Task<string> CreateTicketAsync(DotMarcDbContext context, PsaTicketRequest request, CancellationToken cancellationToken = default);

    /// <summary>Missing means the PSA has no such ticket any more (it was deleted or merged).</summary>
    Task<PsaTicketState> GetTicketStateAsync(DotMarcDbContext context, string ticketId, CancellationToken cancellationToken = default);
    Task CloseTicketAsync(DotMarcDbContext context, string ticketId, string note, CancellationToken cancellationToken = default);

    /// <summary>A link to a ticket with "{0}" where its id goes, or null when the PSA's web address isn't known.</summary>
    Task<string?> GetTicketUrlTemplateAsync(DotMarcDbContext context, CancellationToken cancellationToken = default);
}
