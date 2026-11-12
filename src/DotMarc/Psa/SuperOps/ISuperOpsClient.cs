namespace DotMarc.Psa.SuperOps;

public interface ISuperOpsClient
{
    Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(SuperOpsSettings settings, CancellationToken cancellationToken = default);
    Task<SuperOpsPicklists> GetPicklistsAsync(SuperOpsSettings settings, CancellationToken cancellationToken = default);
    Task<string> CreateTicketAsync(SuperOpsSettings settings, PsaTicketRequest request, CancellationToken cancellationToken = default);

    /// <summary>Null when SuperOps has no such ticket.</summary>
    Task<SuperOpsTicket?> GetTicketAsync(SuperOpsSettings settings, string ticketId, CancellationToken cancellationToken = default);
    Task CloseTicketAsync(SuperOpsSettings settings, string ticketId, string note, CancellationToken cancellationToken = default);
}

/// <summary>What the ticket is in, by the status name SuperOps uses.</summary>
public sealed record SuperOpsTicket(string? Status);

/// <summary>The names SuperOps accepts for a ticket's status, priority, urgency and category. Request types have no list
/// of their own in the API, so that setting is typed in.</summary>
public sealed record SuperOpsPicklists(
    IReadOnlyList<string> Statuses,
    IReadOnlyList<string> Priorities,
    IReadOnlyList<string> Urgencies,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string>? FailedLists = null);

/// <summary>SuperOps accepted the call but reported errors instead of the data. Carries the attributes it said were
/// missing, so callers can react to those without reading the message text.</summary>
public sealed class SuperOpsApiException(string message, System.Net.HttpStatusCode? statusCode, IReadOnlyList<string> missingAttributes)
    : HttpRequestException(message, null, statusCode)
{
    public IReadOnlyList<string> MissingAttributes { get; } = missingAttributes;

    public bool IsMissingValueFor(string attribute) => MissingAttributes.Contains(attribute, StringComparer.OrdinalIgnoreCase);
}
