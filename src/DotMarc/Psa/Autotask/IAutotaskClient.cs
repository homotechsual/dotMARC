namespace DotMarc.Psa.Autotask;

public interface IAutotaskClient
{
    Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(AutotaskSettings settings, CancellationToken cancellationToken = default);
    Task<AutotaskTicketPicklists> GetTicketPicklistsAsync(AutotaskSettings settings, CancellationToken cancellationToken = default);
    Task<string> CreateTicketAsync(AutotaskSettings settings, PsaTicketRequest request, CancellationToken cancellationToken = default);

    /// <summary>The ticket's status, or null when Autotask has no such ticket.</summary>
    Task<int?> GetTicketStatusAsync(AutotaskSettings settings, string ticketId, CancellationToken cancellationToken = default);
    Task CloseTicketAsync(AutotaskSettings settings, string ticketId, string note, CancellationToken cancellationToken = default);
}

/// <summary>The active choices for each ticket field the settings page offers.</summary>
public sealed record AutotaskTicketPicklists(
    IReadOnlyList<PsaOption> Queues,
    IReadOnlyList<PsaOption> TicketTypes,
    IReadOnlyList<PsaOption> IssueTypes,
    IReadOnlyList<PsaOption> Priorities,
    IReadOnlyList<PsaOption> Statuses);
