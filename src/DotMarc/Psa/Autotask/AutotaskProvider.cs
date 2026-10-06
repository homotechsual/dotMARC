using DotMarc.Data;

namespace DotMarc.Psa.Autotask;

/// <summary>Datto Autotask behind the shared PSA contract.</summary>
public sealed class AutotaskProvider(IAutotaskClient client, AutotaskZoneCache zones) : IPsaProvider
{
    public PsaKind Kind => PsaKind.Autotask;

    public async Task<PsaReadiness> GetReadinessAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var settings = await AutotaskSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        return new PsaReadiness(settings.Enabled, MissingSettings(settings));
    }

    public static IReadOnlyList<string> MissingSettings(AutotaskSettings settings)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.Username)) missing.Add("username");
        if (!settings.SecretConfigured) missing.Add("secret");
        if (settings.EffectiveIntegrationCode is null) missing.Add("API tracking identifier");
        if (settings.QueueId is null) missing.Add("queue");
        if (settings.TicketTypeId is null) missing.Add("ticket type");
        if (settings.PriorityId is null) missing.Add("priority");
        if (settings.ClosedStatusId is null) missing.Add("closed status");
        return missing;
    }

    public async Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        await client.ListCompaniesAsync(await AutotaskSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task<string> CreateTicketAsync(DotMarcDbContext context, PsaTicketRequest request, CancellationToken cancellationToken = default) =>
        await client.CreateTicketAsync(await AutotaskSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false), request, cancellationToken).ConfigureAwait(false);

    public async Task<PsaTicketState> GetTicketStateAsync(DotMarcDbContext context, string ticketId, CancellationToken cancellationToken = default)
    {
        var settings = await AutotaskSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        var status = await client.GetTicketStatusAsync(settings, ticketId, cancellationToken).ConfigureAwait(false);
        return status is null ? PsaTicketState.Missing
            : status == settings.ClosedStatusId ? PsaTicketState.Closed
            : PsaTicketState.Open;
    }

    public async Task CloseTicketAsync(DotMarcDbContext context, string ticketId, string note, CancellationToken cancellationToken = default) =>
        await client.CloseTicketAsync(await AutotaskSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false), ticketId, note, cancellationToken).ConfigureAwait(false);

    /// <summary>The web address comes from the zone lookup, so a link is known once dotMARC has called Autotask since it
    /// started (the poller does within minutes). Not yet confirmed against a live tenant.</summary>
    public async Task<string?> GetTicketUrlTemplateAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var settings = await AutotaskSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        return settings.Username is { } username && zones.TryGet(username, out var zone) && zone.WebUrl.Length > 0
            ? zone.WebUrl.TrimEnd('/') + "/Autotask/AutotaskExtend/ExecuteCommand.aspx?Code=OpenTicketDetail&TicketID={0}"
            : null;
    }
}
