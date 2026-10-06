using DotMarc.Data;
using DotMarc.Psa;

namespace DotMarc.Tests.Internal;

/// <summary>An in-memory PSA. Tickets are numbered from <see cref="NextTicketNumber"/>; set <see cref="FailWith"/> to make
/// every call throw, and <see cref="States"/> to choose what a ticket reads as.</summary>
internal sealed class FakePsaProvider(PsaKind kind) : IPsaProvider
{
    public PsaKind Kind { get; } = kind;
    public bool Ready { get; set; } = true;
    public List<string> Missing { get; } = [];
    public Exception? FailWith { get; set; }

    /// <summary>Tickets whose reads and closes throw, as when one ticket has been moved somewhere the API user can't see.</summary>
    public HashSet<string> FailingTickets { get; } = [];

    /// <summary>When true, closing records the call but the ticket still reads as open.</summary>
    public bool IgnoreCloses { get; set; }
    public int NextTicketNumber { get; set; } = 1000;
    public List<PsaTicketRequest> Created { get; } = [];
    public List<string> Closed { get; } = [];
    public Dictionary<string, PsaTicketState> States { get; } = [];
    public List<PsaCompany> Companies { get; } = [];

    public Task<PsaReadiness> GetReadinessAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PsaReadiness(Ready, Missing));

    public Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        return Task.FromResult<IReadOnlyList<PsaCompany>>(Companies);
    }

    public Task<string> CreateTicketAsync(DotMarcDbContext context, PsaTicketRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        Created.Add(request);
        var ticketId = (NextTicketNumber++).ToString(System.Globalization.CultureInfo.InvariantCulture);
        States[ticketId] = PsaTicketState.Open;
        return Task.FromResult(ticketId);
    }

    public Task<PsaTicketState> GetTicketStateAsync(DotMarcDbContext context, string ticketId, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing(ticketId);
        return Task.FromResult(States.TryGetValue(ticketId, out var state) ? state : PsaTicketState.Missing);
    }

    public Task CloseTicketAsync(DotMarcDbContext context, string ticketId, string note, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing(ticketId);
        Closed.Add(ticketId);
        if (!IgnoreCloses)
        {
            States[ticketId] = PsaTicketState.Closed;
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetTicketUrlTemplateAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>($"https://{Kind}.example/ticket/{{0}}");

    private void ThrowIfFailing(string? ticketId = null)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        if (ticketId is not null && FailingTickets.Contains(ticketId))
        {
            throw new HttpRequestException($"Ticket {ticketId} can't be reached.");
        }
    }
}
