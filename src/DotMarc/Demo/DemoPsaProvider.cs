using System.Collections.Concurrent;
using System.Globalization;
using DotMarc.Data;
using DotMarc.Psa;

namespace DotMarc.Demo;

/// <summary>A pretend PSA for the demo instance: always connected, a few sample companies, and tickets that "a tech"
/// closes three minutes after they're raised, so visitors see the poller resolve an alert. Nothing leaves the process.</summary>
public sealed class DemoPsaProvider(PsaKind kind, TimeProvider timeProvider) : IPsaProvider
{
    private static readonly TimeSpan TimeToClose = TimeSpan.FromMinutes(3);
    private static readonly string[] CompanyNames = ["Aurora Retail", "Fabrikam Inc", "Northwind Traders", "Tailspin Toys", "Woodgrove Bank"];

    private readonly ConcurrentDictionary<string, DateTimeOffset> _createdUtc = new();
    private readonly ConcurrentDictionary<string, bool> _closed = new();
    private int _lastTicketNumber = 5000;

    public PsaKind Kind { get; } = kind;

    public Task<PsaReadiness> GetReadinessAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PsaReadiness(true, []));

    public Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        // Numbered from 101: HaloPSA's client 1 is its built-in Unknown client, which the group suggestions skip.
        Task.FromResult<IReadOnlyList<PsaCompany>>(CompanyNames.Select((name, index) => new PsaCompany((index + 101).ToString(CultureInfo.InvariantCulture), name)).ToList());

    public Task<string> CreateTicketAsync(DotMarcDbContext context, PsaTicketRequest request, CancellationToken cancellationToken = default)
    {
        var ticketId = Interlocked.Increment(ref _lastTicketNumber).ToString(CultureInfo.InvariantCulture);
        _createdUtc[ticketId] = timeProvider.GetUtcNow();
        return Task.FromResult(ticketId);
    }

    public Task<PsaTicketState> GetTicketStateAsync(DotMarcDbContext context, string ticketId, CancellationToken cancellationToken = default)
    {
        if (!_createdUtc.TryGetValue(ticketId, out var createdUtc))
        {
            return Task.FromResult(PsaTicketState.Missing);
        }

        var closed = _closed.ContainsKey(ticketId) || timeProvider.GetUtcNow() - createdUtc >= TimeToClose;
        return Task.FromResult(closed ? PsaTicketState.Closed : PsaTicketState.Open);
    }

    public Task CloseTicketAsync(DotMarcDbContext context, string ticketId, string note, CancellationToken cancellationToken = default)
    {
        _closed[ticketId] = true;
        return Task.CompletedTask;
    }

    public Task<string?> GetTicketUrlTemplateAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
