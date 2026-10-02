using DotMarc.Notifications;

namespace DotMarc.Tests.Internal;

internal sealed class NoOpHaloPsaClient : IHaloPsaClient
{
    public Task<IReadOnlyList<HaloClient>> ListClientsAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloClient>>([]);
    public Task<IReadOnlyList<HaloTicketType>> ListTicketTypesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloTicketType>>([]);
    public Task<IReadOnlyList<HaloTicketStatus>> ListStatusesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloTicketStatus>>([]);
    public Task<IReadOnlyList<HaloPriority>> ListPrioritiesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloPriority>>([]);
    public Task<IReadOnlyList<HaloAgent>> ListAgentsAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloAgent>>([]);
    public Task<int> GetTicketStatusAsync(HaloPsaSettings settings, int ticketId, CancellationToken cancellationToken = default) => Task.FromResult(9);
    public Task<string> CreateTicketAsync(HaloPsaSettings settings, int haloClientId, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default) => Task.FromResult("unused");
    public Task CloseTicketAsync(HaloPsaSettings settings, string ticketId, string note, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
