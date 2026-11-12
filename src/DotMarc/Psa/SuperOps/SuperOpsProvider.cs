using DotMarc.Data;

namespace DotMarc.Psa.SuperOps;

/// <summary>SuperOps PSA behind the shared PSA contract.</summary>
public sealed class SuperOpsProvider(ISuperOpsClient client) : IPsaProvider
{
    /// <summary>Statuses that always mean a ticket is done, whatever closed status is chosen: SuperOps's own built-in ones.</summary>
    private static readonly string[] DoneStatuses = ["Resolved", "Closed"];

    public PsaKind Kind => PsaKind.SuperOps;

    public async Task<PsaReadiness> GetReadinessAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var settings = await SuperOpsSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        return new PsaReadiness(settings.Enabled, MissingSettings(settings));
    }

    public static IReadOnlyList<string> MissingSettings(SuperOpsSettings settings)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.Subdomain)) missing.Add("subdomain");
        if (!settings.ApiTokenConfigured) missing.Add("API token");
        if (string.IsNullOrWhiteSpace(settings.RequestType)) missing.Add("request type");
        if (string.IsNullOrWhiteSpace(settings.NewStatus)) missing.Add("new ticket status");
        if (string.IsNullOrWhiteSpace(settings.ClosedStatus)) missing.Add("closed status");
        return missing;
    }

    public async Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        await client.ListCompaniesAsync(await SuperOpsSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task<string> CreateTicketAsync(DotMarcDbContext context, PsaTicketRequest request, CancellationToken cancellationToken = default) =>
        await client.CreateTicketAsync(await SuperOpsSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false), request, cancellationToken).ConfigureAwait(false);

    public async Task<PsaTicketState> GetTicketStateAsync(DotMarcDbContext context, string ticketId, CancellationToken cancellationToken = default)
    {
        var settings = await SuperOpsSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        var ticket = await client.GetTicketAsync(settings, ticketId, cancellationToken).ConfigureAwait(false);
        return ticket is null ? PsaTicketState.Missing : IsDone(ticket.Status, settings) ? PsaTicketState.Closed : PsaTicketState.Open;
    }

    public async Task CloseTicketAsync(DotMarcDbContext context, string ticketId, string note, CancellationToken cancellationToken = default) =>
        await client.CloseTicketAsync(await SuperOpsSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false), ticketId, note, cancellationToken).ConfigureAwait(false);

    /// <summary>The ticket's web address isn't known for sure, so tickets show their id without a link.</summary>
    public Task<string?> GetTicketUrlTemplateAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    private static bool IsDone(string? status, SuperOpsSettings settings) =>
        !string.IsNullOrWhiteSpace(status)
        && (DoneStatuses.Contains(status.Trim(), StringComparer.OrdinalIgnoreCase)
            || string.Equals(status.Trim(), settings.ClosedStatus, StringComparison.OrdinalIgnoreCase));
}
