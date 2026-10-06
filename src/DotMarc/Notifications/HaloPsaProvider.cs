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

    /// <summary>What ticketing needs: the connection. The ticket type and priority are left to Halo's own defaults when
    /// blank, as they always have been, so an install that never chose them keeps raising tickets. The closed status and
    /// webhook secret aren't needed to raise tickets; the integration test checks them (see
    /// <see cref="MissingForIntegrationTest"/>).</summary>
    public static IReadOnlyList<string> MissingSettings(HaloPsaSettings settings)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.AuthServerUrl)) missing.Add("auth server URL");
        if (string.IsNullOrWhiteSpace(settings.ResourceServerUrl)) missing.Add("resource server URL");
        if (string.IsNullOrWhiteSpace(settings.ClientId)) missing.Add("client ID");
        if (!settings.ClientSecretConfigured) missing.Add("client secret");
        return missing;
    }

    /// <summary>What the integration test needs beyond ticketing: a closed status to close with and recognise, and a
    /// webhook secret, since the test waits for Halo's webhook.</summary>
    public static IReadOnlyList<string> MissingForIntegrationTest(HaloPsaSettings settings)
    {
        var missing = new List<string>();
        if (settings.ClosedStatusId is null) missing.Add("closed status");
        if (string.IsNullOrWhiteSpace(settings.WebhookSecret)) missing.Add("webhook secret");
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
