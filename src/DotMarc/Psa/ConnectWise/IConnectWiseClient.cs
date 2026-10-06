namespace DotMarc.Psa.ConnectWise;

public interface IConnectWiseClient
{
    Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(ConnectWiseSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PsaOption>> ListBoardsAsync(ConnectWiseSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PsaOption>> ListBoardStatusesAsync(ConnectWiseSettings settings, int boardId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PsaOption>> ListBoardTypesAsync(ConnectWiseSettings settings, int boardId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PsaOption>> ListPrioritiesAsync(ConnectWiseSettings settings, CancellationToken cancellationToken = default);
    Task<string> CreateTicketAsync(ConnectWiseSettings settings, PsaTicketRequest request, CancellationToken cancellationToken = default);

    /// <summary>Null when ConnectWise has no such ticket.</summary>
    Task<ConnectWiseTicket?> GetTicketAsync(ConnectWiseSettings settings, string ticketId, CancellationToken cancellationToken = default);
    Task CloseTicketAsync(ConnectWiseSettings settings, string ticketId, string note, CancellationToken cancellationToken = default);
}

public sealed record ConnectWiseTicket(bool ClosedFlag, int? StatusId);
