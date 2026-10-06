using DotMarc.Data;

namespace DotMarc.Psa.ConnectWise;

/// <summary>ConnectWise PSA behind the shared PSA contract.</summary>
public sealed class ConnectWiseProvider(IConnectWiseClient client) : IPsaProvider
{
    public PsaKind Kind => PsaKind.ConnectWise;

    public async Task<PsaReadiness> GetReadinessAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var settings = await ConnectWiseSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        return new PsaReadiness(settings.Enabled, MissingSettings(settings));
    }

    public static IReadOnlyList<string> MissingSettings(ConnectWiseSettings settings)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.SiteUrl)) missing.Add("site URL");
        if (string.IsNullOrWhiteSpace(settings.CompanyId)) missing.Add("company ID");
        if (string.IsNullOrWhiteSpace(settings.PublicKey)) missing.Add("public key");
        if (!settings.PrivateKeyConfigured) missing.Add("private key");
        if (settings.EffectiveClientId is null) missing.Add("client ID");
        if (settings.BoardId is null) missing.Add("board");
        if (settings.StatusId is null) missing.Add("new ticket status");
        if (settings.PriorityId is null) missing.Add("priority");
        if (settings.ClosedStatusId is null) missing.Add("closed status");
        return missing;
    }

    public async Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        await client.ListCompaniesAsync(await ConnectWiseSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task<string> CreateTicketAsync(DotMarcDbContext context, PsaTicketRequest request, CancellationToken cancellationToken = default) =>
        await client.CreateTicketAsync(await ConnectWiseSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false), request, cancellationToken).ConfigureAwait(false);

    public async Task<PsaTicketState> GetTicketStateAsync(DotMarcDbContext context, string ticketId, CancellationToken cancellationToken = default)
    {
        var settings = await ConnectWiseSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        var ticket = await client.GetTicketAsync(settings, ticketId, cancellationToken).ConfigureAwait(false);
        if (ticket is null)
        {
            return PsaTicketState.Missing;
        }

        // ConnectWise marks every closed-type status with closedFlag, so a tech closing with any of them counts.
        return ticket.ClosedFlag || ticket.StatusId == settings.ClosedStatusId ? PsaTicketState.Closed : PsaTicketState.Open;
    }

    public async Task CloseTicketAsync(DotMarcDbContext context, string ticketId, string note, CancellationToken cancellationToken = default) =>
        await client.CloseTicketAsync(await ConnectWiseSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false), ticketId, note, cancellationToken).ConfigureAwait(false);

    /// <summary>The web app is on the API host without its "api-" prefix (api-eu.myconnectwise.net serves
    /// eu.myconnectwise.net). Not yet confirmed against a live tenant.</summary>
    public async Task<string?> GetTicketUrlTemplateAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var settings = await ConnectWiseSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.SiteUrl))
        {
            return null;
        }

        var webHost = settings.SiteUrl.StartsWith("api-", StringComparison.OrdinalIgnoreCase) ? settings.SiteUrl["api-".Length..] : settings.SiteUrl;
        return $"https://{webHost}/v4_6_release/ConnectWise.aspx?routeTo=ServiceFV&recid={{0}}";
    }
}
