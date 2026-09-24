// src/DotMarc/Notifications/HaloPsaClient.cs
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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

    // Matches what HttpContent.ReadFromJsonAsync used before these reads went through ReadJsonAsync.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<HaloClient>> ListClientsAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, settings, "Client", null, cancellationToken).ConfigureAwait(false);
        var payload = await ReadJsonAsync<ClientListResponse>(response, "Client", cancellationToken).ConfigureAwait(false);
        return payload?.Clients.Select(c => new HaloClient(c.Id, c.Name)).ToList() ?? [];
    }

    public async Task<IReadOnlyList<HaloTicketType>> ListTicketTypesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, settings, "TicketType", null, cancellationToken).ConfigureAwait(false);
        var payload = await ReadJsonAsync<List<IdNameEntry>>(response, "TicketType", cancellationToken).ConfigureAwait(false);
        return payload?.Select(e => new HaloTicketType(e.Id, e.Name)).ToList() ?? [];
    }

    public async Task<IReadOnlyList<HaloTicketStatus>> ListStatusesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, settings, "Status", null, cancellationToken).ConfigureAwait(false);
        var payload = await ReadJsonAsync<List<IdNameEntry>>(response, "Status", cancellationToken).ConfigureAwait(false);
        return payload?.Select(e => new HaloTicketStatus(e.Id, e.Name)).ToList() ?? [];
    }

    /// <summary>GET /api/Priority doesn't return a plain list of priorities: it returns one row per
    /// priority per SLA. Each row's own <c>id</c> is a GUID; the number a ticket's <c>priority_id</c>
    /// takes is <c>priorityid</c>, which repeats across SLAs. So this de-duplicates on
    /// <c>priorityid</c> and skips hidden rows, giving one selectable entry per priority.</summary>
    public async Task<IReadOnlyList<HaloPriority>> ListPrioritiesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, settings, "Priority", null, cancellationToken).ConfigureAwait(false);
        var entries = await ReadJsonAsync<List<PriorityEntry>>(response, "Priority", cancellationToken, ResolvePriorityIds).ConfigureAwait(false) ?? [];
        return entries
            .Where(e => !e.IsHidden)
            .GroupBy(e => e.ResolvedId)
            .Select(group => new HaloPriority(group.Key, group.First().Name))
            .OrderBy(priority => priority.Id)
            .ToList();
    }

    private static void ResolvePriorityIds(List<PriorityEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.PriorityId is { } priorityId)
            {
                entry.ResolvedId = priorityId;
            }
            else if (TryGetWholeNumber(entry.Id, out var id))
            {
                // No priorityid: treat a numeric id as the priority's number.
                entry.ResolvedId = id;
            }
            else
            {
                throw new JsonException($"The priority \"{entry.Name}\" has neither a priorityid nor a numeric id.");
            }
        }
    }

    private static bool TryGetWholeNumber(JsonElement element, out int value)
    {
        value = 0;
        decimal number = 0;
        const System.Globalization.NumberStyles style = System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint
            | System.Globalization.NumberStyles.AllowLeadingWhite | System.Globalization.NumberStyles.AllowTrailingWhite;

        var parsed = element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDecimal(out number),
            JsonValueKind.String => decimal.TryParse(element.GetString(), style, System.Globalization.CultureInfo.InvariantCulture, out number),
            _ => false
        };

        if (!parsed || number != decimal.Truncate(number) || number < int.MinValue || number > int.MaxValue)
        {
            return false;
        }

        value = (int)number;
        return true;
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "...";

    /// <summary>Reads one of the lookup lists. When Halo's answer isn't the shape dotMARC expects,
    /// the error says so along with the start of what Halo actually sent, so a mismatch can be
    /// diagnosed from the message alone instead of a bare "could not be converted". These endpoints
    /// return only ids and names (no secrets), so quoting the opening is safe. A JsonException
    /// thrown by <paramref name="validate"/> is reported the same way.</summary>
    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, string resource, CancellationToken cancellationToken, Action<T>? validate = null)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var value = JsonSerializer.Deserialize<T>(body, JsonOptions);
            if (value is not null)
            {
                validate?.Invoke(value);
            }

            return value;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"HaloPSA's {resource} response wasn't in the expected format. {exception.Message} It began: {Truncate(body)}", exception);
        }
    }

    public async Task<string> CreateTicketAsync(HaloPsaSettings settings, int haloClientId, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default)
    {
        var ticket = new CreateTicketRequest(title, $"{message}\n\nDomain: {domainName}\nAlert type: {alertType}\nRaised automatically by dotMARC.", haloClientId, settings.TicketTypeId, settings.DefaultPriorityId);

        // Halo's POST endpoints take an array of records, even for a single ticket; a bare object is
        // refused with "requires a JSON array".
        using var response = await SendAsync(HttpMethod.Post, settings, "Tickets", new[] { ticket }, cancellationToken).ConfigureAwait(false);
        var created = await ReadJsonAsync<JsonElement>(response, "Tickets", cancellationToken).ConfigureAwait(false);

        // Halo answers with the created ticket, though some versions wrap it in a one-item array.
        var createdTicket = created.ValueKind == JsonValueKind.Array && created.GetArrayLength() > 0 ? created[0] : created;
        if (createdTicket.ValueKind != JsonValueKind.Object || !createdTicket.TryGetProperty("id", out var id) || !TryGetWholeNumber(id, out var ticketId))
        {
            throw new InvalidDataException($"HaloPSA created a ticket but its response didn't include the ticket's id. It began: {Truncate(createdTicket.ToString())}");
        }

        return ticketId.ToString();
    }

    public async Task CloseTicketAsync(HaloPsaSettings settings, string ticketId, string note, CancellationToken cancellationToken = default)
    {
        // Halo updates a ticket by posting the changed fields, with the ticket's id, to Tickets.
        var update = new CloseTicketRequest(int.Parse(ticketId), settings.ClosedStatusId, note);
        using var response = await SendAsync(HttpMethod.Post, settings, "Tickets", new[] { update }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, HaloPsaSettings settings, string relativePath, object? body, CancellationToken cancellationToken)
    {
        var clientSecret = await _secretStore.GetSecretAsync(HaloPsaSettings.SecretStoreKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("HaloPSA client secret is not configured.");

        var response = await SendOnceAsync(method, settings, relativePath, body, clientSecret, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The cached token may have been revoked early on Halo's side. Invalidate it and retry
            // exactly once with a freshly-acquired token - never more, to avoid looping forever
            // against a persistently-invalid credential.
            response.Dispose();
            await _tokenCache.InvalidateAsync(settings, cancellationToken).ConfigureAwait(false);
            response = await SendOnceAsync(method, settings, relativePath, body, clientSecret, cancellationToken).ConfigureAwait(false);
        }

        await ThrowIfNotSuccessAsync(response, method, relativePath, _tokenCache.GrantedScopeFor(settings), cancellationToken).ConfigureAwait(false);
        return response;
    }

    /// <summary>A bare "400 Bad Request" says nothing about which field Halo objected to, so the
    /// error carries the start of Halo's own explanation. The request body is deliberately not
    /// included; Halo's error responses describe the problem and don't echo credentials.</summary>
    private static async Task ThrowIfNotSuccessAsync(HttpResponseMessage response, HttpMethod method, string relativePath, string? grantedScope, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = response.StatusCode;
        var reason = response.ReasonPhrase;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var challenge = string.Join(", ", response.Headers.WwwAuthenticate.Select(header => header.ToString()));
        response.Dispose();

        var explanation = string.IsNullOrWhiteSpace(body) ? "" : $" Halo said: {Truncate(body)}";
        if (!string.IsNullOrWhiteSpace(challenge))
        {
            explanation += $" Halo's challenge: {challenge}.";
        }

        // Halo often answers a permission problem with an empty 403, so say what the token was granted.
        if (status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            explanation += $" Scope granted to the token: {(string.IsNullOrWhiteSpace(grantedScope) ? "not reported by Halo" : grantedScope)}.";
        }

        throw new HttpRequestException($"HaloPSA returned {(int)status} {reason} for {method} {relativePath}.{explanation}", null, status);
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

    private sealed class PriorityEntry
    {
        [JsonPropertyName("id")]
        public JsonElement Id { get; set; }

        [JsonPropertyName("priorityid"), JsonConverter(typeof(FlexibleInt32Converter))]
        public int? PriorityId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("ishidden")]
        public bool IsHidden { get; set; }

        [JsonIgnore]
        public int ResolvedId { get; set; }
    }

    private sealed record IdNameEntry(
        [property: JsonPropertyName("id"), JsonConverter(typeof(FlexibleInt32Converter))] int Id,
        [property: JsonPropertyName("name")] string Name);
    private sealed record ClientListResponse([property: JsonPropertyName("clients")] List<IdNameEntry> Clients);
    private sealed record CreateTicketRequest(
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("details")] string Details,
        [property: JsonPropertyName("client_id")] int ClientId,
        [property: JsonPropertyName("tickettype_id")] int? TicketTypeId,
        [property: JsonPropertyName("priority_id")] int? PriorityId);
    private sealed record CloseTicketRequest(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("status_id")] int? StatusId,
        [property: JsonPropertyName("note")] string Note);
}
