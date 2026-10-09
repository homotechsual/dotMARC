using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotMarc.Notifications;

namespace DotMarc.Psa.Autotask;

/// <summary>Autotask REST API v1.0. The API lives in a zone per tenant, found from the API user's username. Every call
/// sends the ApiIntegrationCode (tracking identifier), UserName and Secret headers.</summary>
public sealed class AutotaskClient(HttpClient httpClient, ISecretStore secretStore, AutotaskZoneCache zones, TimeProvider timeProvider) : IAutotaskClient
{
    private const string ZoneLookupUrl = "https://webservices.autotask.net/atservicesrest/v1.0/zoneInformation?user=";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(AutotaskSettings settings, CancellationToken cancellationToken = default)
    {
        var search = JsonSerializer.Serialize(new
        {
            filter = new[] { new { op = "eq", field = "isActive", value = true } },
            IncludeFields = new[] { "id", "companyName" },
        });
        var companies = new List<PsaCompany>();
        string? next = "Companies/query?search=" + Uri.EscapeDataString(search);
        while (next is not null)
        {
            using var response = await SendAsync(settings, HttpMethod.Get, next, null, cancellationToken).ConfigureAwait(false);
            var page = await response.Content.ReadFromJsonAsync<CompanyPage>(JsonOptions, cancellationToken).ConfigureAwait(false);
            companies.AddRange(page?.Items.Select(item => new PsaCompany(item.Id.ToString(CultureInfo.InvariantCulture), item.CompanyName)) ?? []);
            next = page?.PageDetails?.NextPageUrl;
        }

        return companies.OrderBy(company => company.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<AutotaskTicketPicklists> GetTicketPicklistsAsync(AutotaskSettings settings, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(settings, HttpMethod.Get, "Tickets/entityInformation/fields", null, cancellationToken).ConfigureAwait(false);
        var info = await response.Content.ReadFromJsonAsync<FieldInfo>(JsonOptions, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<PsaOption> Options(string fieldName) =>
            info?.Fields.FirstOrDefault(field => string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase))?.PicklistValues?
                .Where(value => value.IsActive && int.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                .Select(value => new PsaOption(int.Parse(value.Value, CultureInfo.InvariantCulture), value.Label))
                .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];

        return new AutotaskTicketPicklists(Options("queueID"), Options("ticketType"), Options("issueType"), Options("priority"), Options("status"));
    }

    public async Task<string> CreateTicketAsync(AutotaskSettings settings, PsaTicketRequest request, CancellationToken cancellationToken = default)
    {
        var ticket = new CreateTicketBody(
            int.Parse(request.CompanyId, CultureInfo.InvariantCulture),
            Truncate(request.Title, 255),
            Truncate(request.Body, 8000),
            settings.QueueId!.Value,
            settings.TicketTypeId!.Value,
            settings.IssueTypeId,
            settings.PriorityId!.Value,
            AutotaskSettings.NewStatus,
            // Autotask requires a due date unless the ticket category sets one.
            timeProvider.GetUtcNow().AddDays(1));
        using var response = await SendAsync(settings, HttpMethod.Post, "Tickets", JsonContent.Create(ticket, options: JsonOptions), cancellationToken).ConfigureAwait(false);
        var created = await response.Content.ReadFromJsonAsync<ItemId>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Autotask created a ticket but didn't say its id.");
        return created.Value.ToString(CultureInfo.InvariantCulture);
    }

    public async Task<int?> GetTicketStatusAsync(AutotaskSettings settings, string ticketId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(settings, HttpMethod.Get, $"Tickets/{Uri.EscapeDataString(ticketId)}", null, cancellationToken).ConfigureAwait(false);
            var ticket = await response.Content.ReadFromJsonAsync<TicketItem>(JsonOptions, cancellationToken).ConfigureAwait(false);
            return ticket?.Item?.Status;
        }
        catch (TicketRequestNotFoundException)
        {
            return null;
        }
    }

    public async Task CloseTicketAsync(AutotaskSettings settings, string ticketId, string note, CancellationToken cancellationToken = default)
    {
        // Autotask's ticket notes need tenant-specific note type and publish values, so the note goes in the ticket's
        // Resolution, set in the same update as the status.
        var update = new CloseTicketBody(long.Parse(ticketId, CultureInfo.InvariantCulture), settings.ClosedStatusId ?? AutotaskSettings.CompleteStatus, note);
        using var response = await SendAsync(settings, HttpMethod.Patch, "Tickets", JsonContent.Create(update, options: JsonOptions), cancellationToken).ConfigureAwait(false);
    }

    private async Task<AutotaskZone> GetZoneAsync(AutotaskSettings settings, CancellationToken cancellationToken)
    {
        var username = settings.Username ?? throw new InvalidOperationException("The Autotask username isn't saved.");
        if (zones.TryGet(username, out var cached))
        {
            return cached;
        }

        using var response = await httpClient.GetAsync(ZoneLookupUrl + Uri.EscapeDataString(username), cancellationToken).ConfigureAwait(false);
        var zone = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ZoneInformation>(JsonOptions, cancellationToken).ConfigureAwait(false) : null;
        if (zone?.Url is null)
        {
            throw new HttpRequestException("Autotask couldn't find a zone for that username. Check it's the API user's username.", null, response.StatusCode);
        }

        var found = new AutotaskZone(zone.Url.TrimEnd('/') + "/V1.0/", zone.WebUrl ?? "");
        zones.Set(username, found);
        return found;
    }

    /// <param name="path">Relative to the zone's API root, or an absolute nextPageUrl Autotask returned.</param>
    private async Task<HttpResponseMessage> SendAsync(AutotaskSettings settings, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        var integrationCode = settings.EffectiveIntegrationCode ?? throw new InvalidOperationException("Autotask needs an API tracking identifier before dotMARC can call it.");
        var secret = await secretStore.GetSecretAsync(AutotaskSettings.SecretStoreKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Autotask secret isn't saved.");
        var zone = await GetZoneAsync(settings, cancellationToken).ConfigureAwait(false);

        var url = path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? path : zone.ApiUrl + path;
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Add("ApiIntegrationCode", integrationCode);
        request.Headers.Add("UserName", settings.Username);
        request.Headers.Add("Secret", secret);
        var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var status = response.StatusCode;
        response.Dispose();
        if (status == HttpStatusCode.Unauthorized)
        {
            // The tenant may have moved zone, or the user's details changed: look the zone up again next time.
            zones.Forget(settings.Username!);
        }

        if (status == HttpStatusCode.NotFound && method == HttpMethod.Get && path.StartsWith("Tickets/", StringComparison.Ordinal))
        {
            throw new TicketRequestNotFoundException();
        }

        var advice = status switch
        {
            HttpStatusCode.Unauthorized => "Autotask refused the sign-in: check the username, secret and API tracking identifier.",
            HttpStatusCode.Forbidden => "Autotask refused the request: check the API user's security level allows it.",
            _ => $"Autotask returned {(int)status} for {method} {path.Split('?')[0]}."
        };

        // Autotask's error bodies describe the problem and don't echo credentials.
        throw new HttpRequestException(string.IsNullOrWhiteSpace(body) || body == "{}" ? advice : $"{advice} Autotask said: {Truncate(body, 300)}", null, status);
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>A ticket lookup that Autotask answered 404, which means the ticket is gone.</summary>
    private sealed class TicketRequestNotFoundException : Exception;

    private sealed record ZoneInformation(string? Url, string? WebUrl);
    private sealed record CompanyItem(long Id, string CompanyName);
    private sealed record PageDetails(string? NextPageUrl);
    private sealed record CompanyPage(List<CompanyItem> Items, PageDetails? PageDetails);
    private sealed record PicklistValue(string Value, string Label, bool IsActive);
    private sealed record Field(string Name, List<PicklistValue>? PicklistValues);
    private sealed record FieldInfo(List<Field> Fields);
    private sealed record ItemId([property: JsonPropertyName("itemId")] long Value);
    private sealed record TicketState(long Id, int Status);
    private sealed record TicketItem(TicketState? Item);
    private sealed record CloseTicketBody(long Id, int Status, string Resolution);
    private sealed record CreateTicketBody(
        [property: JsonPropertyName("companyID")] int CompanyId,
        string Title,
        string Description,
        [property: JsonPropertyName("queueID")] int QueueId,
        int TicketType,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? IssueType,
        int Priority,
        int Status,
        DateTimeOffset DueDateTime);
}
