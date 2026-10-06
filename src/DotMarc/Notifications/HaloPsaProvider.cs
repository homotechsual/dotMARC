using System.Globalization;
using System.Net;
using DotMarc.Data;
using DotMarc.Psa;

namespace DotMarc.Notifications;

/// <summary>HaloPSA behind the shared PSA contract. The Halo client and settings are unchanged; this adapts them.</summary>
public sealed class HaloPsaProvider(IHaloPsaClient haloClient) : IPsaProvider
{
    public PsaKind Kind => PsaKind.HaloPsa;

    public async Task<PsaReadiness> GetReadinessAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var settings = await HaloPsaSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        return new PsaReadiness(settings.Enabled, MissingSettings(settings));
    }

    /// <summary>What ticketing needs. The webhook secret isn't here: tickets still close back through the poller without
    /// it. The integration test checks it separately.</summary>
    public static IReadOnlyList<string> MissingSettings(HaloPsaSettings settings)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.AuthServerUrl)) missing.Add("auth server URL");
        if (string.IsNullOrWhiteSpace(settings.ResourceServerUrl)) missing.Add("resource server URL");
        if (string.IsNullOrWhiteSpace(settings.ClientId)) missing.Add("client ID");
        if (!settings.ClientSecretConfigured) missing.Add("client secret");
        if (settings.TicketTypeId is null) missing.Add("ticket type");
        if (settings.DefaultPriorityId is null) missing.Add("default priority");
        if (settings.ClosedStatusId is null) missing.Add("closed status");
        return missing;
    }

    public async Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var settings = await HaloPsaSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        var clients = await haloClient.ListClientsAsync(settings, cancellationToken).ConfigureAwait(false);
        return clients.Select(client => new PsaCompany(client.Id.ToString(CultureInfo.InvariantCulture), client.Name)).ToList();
    }

    public async Task<string> CreateTicketAsync(DotMarcDbContext context, PsaTicketRequest request, CancellationToken cancellationToken = default)
    {
        var settings = await HaloPsaSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        var clientId = int.Parse(request.CompanyId, CultureInfo.InvariantCulture);
        return await haloClient.CreateTicketAsync(settings, clientId, request.DomainName, request.AlertType, request.Title, request.Message, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PsaTicketState> GetTicketStateAsync(DotMarcDbContext context, string ticketId, CancellationToken cancellationToken = default)
    {
        var settings = await HaloPsaSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        try
        {
            var statusId = await haloClient.GetTicketStatusAsync(settings, int.Parse(ticketId, CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            return statusId == settings.ClosedStatusId ? PsaTicketState.Closed : PsaTicketState.Open;
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return PsaTicketState.Missing;
        }
    }

    public async Task CloseTicketAsync(DotMarcDbContext context, string ticketId, string note, CancellationToken cancellationToken = default)
    {
        var settings = await HaloPsaSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        await haloClient.CloseTicketAsync(settings, ticketId, note, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetTicketUrlTemplateAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var settings = await HaloPsaSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.ResourceServerUrl))
        {
            return null;
        }

        // The resource server is the API, at /api beside the web app.
        var webRoot = settings.ResourceServerUrl.TrimEnd('/');
        if (webRoot.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            webRoot = webRoot[..^"/api".Length];
        }

        return webRoot + "/ticket?id={0}";
    }
}
