// src/DotMarc/Notifications/HaloPsaClient.cs
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DotMarc.Notifications;

public sealed class HaloPsaClient : IHaloPsaClient
{
    private readonly HttpClient _httpClient;
    private readonly ISecretStore _secretStore;
    private readonly HaloPsaTokenCache _tokenCache;

    public HaloPsaClient(HttpClient httpClient, ISecretStore secretStore, HaloPsaTokenCache tokenCache)
    {
        _httpClient = httpClient;
        _secretStore = secretStore;
        _tokenCache = tokenCache;
    }

    public async Task<IReadOnlyList<HaloClient>> ListClientsAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, settings, "Client", null, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<ClientListResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return payload?.Clients.Select(c => new HaloClient(c.Id, c.Name)).ToList() ?? [];
    }

    public async Task<IReadOnlyList<HaloTicketType>> ListTicketTypesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, settings, "TicketType", null, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<List<IdNameEntry>>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return payload?.Select(e => new HaloTicketType(e.Id, e.Name)).ToList() ?? [];
    }

    public async Task<IReadOnlyList<HaloTicketStatus>> ListStatusesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, settings, "Status", null, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<List<IdNameEntry>>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return payload?.Select(e => new HaloTicketStatus(e.Id, e.Name)).ToList() ?? [];
    }

    public async Task<IReadOnlyList<HaloPriority>> ListPrioritiesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, settings, "Priority", null, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<List<IdNameEntry>>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return payload?.Select(e => new HaloPriority(e.Id, e.Name)).ToList() ?? [];
    }

    public async Task<string> CreateTicketAsync(HaloPsaSettings settings, int haloClientId, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default)
    {
        var body = new CreateTicketRequest(title, $"{message}\n\nDomain: {domainName}\nAlert type: {alertType}\nRaised automatically by dotMARC.", haloClientId, settings.TicketTypeId, settings.DefaultPriorityId);
        using var response = await SendAsync(HttpMethod.Post, settings, "Tickets", body, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<CreateTicketResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return payload!.Id.ToString();
    }

    public async Task CloseTicketAsync(HaloPsaSettings settings, string ticketId, string note, CancellationToken cancellationToken = default)
    {
        var body = new CloseTicketRequest(int.Parse(ticketId), settings.ClosedStatusId, note);
        using var response = await SendAsync(HttpMethod.Post, settings, $"Tickets/{ticketId}", body, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, HaloPsaSettings settings, string relativePath, object? body, CancellationToken cancellationToken)
    {
        var clientSecret = await _secretStore.GetSecretAsync(HaloPsaSettings.SecretStoreKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("HaloPSA client secret is not configured.");

        var response = await SendOnceAsync(method, settings, relativePath, body, clientSecret, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The cached token may have been revoked early on Halo's side. Invalidate it and retry
            // exactly once with a freshly-acquired token — never more, to avoid looping forever
            // against a persistently-invalid credential.
            response.Dispose();
            await _tokenCache.InvalidateAsync(settings, cancellationToken).ConfigureAwait(false);
            response = await SendOnceAsync(method, settings, relativePath, body, clientSecret, cancellationToken).ConfigureAwait(false);
        }

        response.EnsureSuccessStatusCode();
        return response;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, HaloPsaSettings settings, string relativePath, object? body, string clientSecret, CancellationToken cancellationToken)
    {
        var token = await _tokenCache.GetTokenAsync(_httpClient, settings, clientSecret, cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(method, $"{settings.ResourceServerUrl!.TrimEnd('/')}/{relativePath}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private sealed record IdNameEntry([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("name")] string Name);
    private sealed record ClientListResponse([property: JsonPropertyName("clients")] List<IdNameEntry> Clients);
    private sealed record CreateTicketRequest(
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("details")] string Details,
        [property: JsonPropertyName("client_id")] int ClientId,
        [property: JsonPropertyName("tickettype_id")] int? TicketTypeId,
        [property: JsonPropertyName("priority_id")] int? PriorityId);
    private sealed record CreateTicketResponse([property: JsonPropertyName("id")] int Id);
    private sealed record CloseTicketRequest(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("status_id")] int? StatusId,
        [property: JsonPropertyName("note")] string Note);
}
