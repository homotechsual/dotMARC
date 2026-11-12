using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DotMarc.Notifications;

namespace DotMarc.Psa.SuperOps;

/// <summary>SuperOps's GraphQL API. Every call is a POST to one endpoint, signed in with the API token as a bearer token
/// and the account's subdomain in the CustomerSubDomain header. SuperOps answers 200 even when a query fails, with the
/// problem in an "errors" array, so those are read as well as the HTTP status.</summary>
public sealed class SuperOpsClient(HttpClient httpClient, ISecretStore secretStore) : ISuperOpsClient
{
    private const int PageSize = 200;
    private const int MaxSubjectLength = 250;
    private const int MaxClientPages = 500;

    public async Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(SuperOpsSettings settings, CancellationToken cancellationToken = default)
    {
        const string query = """
            query GetClientList($input: ListInfoInput!) {
              getClientList(input: $input) {
                clients { accountId name }
                listInfo { hasMore }
              }
            }
            """;
        var companies = new List<PsaCompany>();
        for (var page = 1; page <= MaxClientPages; page++)
        {
            var data = await ExecuteAsync(settings, query, new { input = new { page, pageSize = PageSize } }, "getClientList", cancellationToken).ConfigureAwait(false);
            var list = data.GetProperty("getClientList");
            var pageIsEmpty = true;
            if (list.TryGetProperty("clients", out var clients) && clients.ValueKind == JsonValueKind.Array)
            {
                pageIsEmpty = clients.GetArrayLength() == 0;
                companies.AddRange(clients.EnumerateArray()
                    .Select(client => new PsaCompany(Text(client, "accountId") ?? string.Empty, Text(client, "name") ?? string.Empty))
                    .Where(company => company.Id.Length > 0 && company.Name.Length > 0));
            }

            // An empty page ends the loop too, so an API that keeps saying hasMore can't hold it forever.
            if (pageIsEmpty || !(list.TryGetProperty("listInfo", out var info) && info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("hasMore", out var hasMore) && hasMore.ValueKind == JsonValueKind.True))
            {
                break;
            }
        }

        return companies.OrderBy(company => company.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<SuperOpsPicklists> GetPicklistsAsync(SuperOpsSettings settings, CancellationToken cancellationToken = default)
    {
        const string query = """
            query GetTicketPicklists {
              getStatusList { name }
              getPriorityList { name }
              getUrgencyList { name }
              getCategoryList { name }
            }
            """;

        // Any one list can fail on its own (an account without the feature), and the rest are still worth showing.
        var data = await ExecuteAsync(settings, query, null, null, cancellationToken).ConfigureAwait(false);
        var failed = new[] { ("statuses", "getStatusList"), ("priorities", "getPriorityList"), ("urgencies", "getUrgencyList"), ("categories", "getCategoryList") }
            .Where(list => !(data.TryGetProperty(list.Item2, out var value) && value.ValueKind == JsonValueKind.Array))
            .Select(list => list.Item1)
            .ToList();
        return new SuperOpsPicklists(Names(data, "getStatusList"), Names(data, "getPriorityList"), Names(data, "getUrgencyList"), Names(data, "getCategoryList"), failed);
    }

    public async Task<string> CreateTicketAsync(SuperOpsSettings settings, PsaTicketRequest request, CancellationToken cancellationToken = default)
    {
        const string query = """
            mutation CreateTicket($input: CreateTicketInput!) {
              createTicket(input: $input) { ticketId }
            }
            """;
        var input = new Dictionary<string, object?>
        {
            ["subject"] = Truncate(request.Title, MaxSubjectLength),
            ["description"] = ToHtml(request.Body),
            ["client"] = new { accountId = request.CompanyId },
            ["status"] = settings.NewStatus,
            ["requestType"] = settings.RequestType,
            ["source"] = "INTEGRATION",
        };
        AddIfSet(input, "priority", settings.Priority);
        AddIfSet(input, "urgency", settings.Urgency);
        AddIfSet(input, "category", settings.Category);

        var data = await ExecuteAsync(settings, query, new { input }, "createTicket", cancellationToken).ConfigureAwait(false);
        return Text(data.GetProperty("createTicket"), "ticketId")
            ?? throw new InvalidDataException("SuperOps created a ticket but didn't say its id.");
    }

    public async Task<SuperOpsTicket?> GetTicketAsync(SuperOpsSettings settings, string ticketId, CancellationToken cancellationToken = default)
    {
        const string query = """
            query GetTicket($input: ListInfoInput!) {
              getTicketList(input: $input) {
                tickets { ticketId status }
              }
            }
            """;
        var input = new
        {
            page = 1,
            pageSize = 1,
            condition = new { attribute = "ticketId", @operator = "is", value = ticketId },
        };
        var data = await ExecuteAsync(settings, query, new { input }, "getTicketList", cancellationToken).ConfigureAwait(false);
        var list = data.GetProperty("getTicketList");
        if (!list.TryGetProperty("tickets", out var tickets) || tickets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("SuperOps returned a ticket list without a tickets array.");
        }

        // The filter is an exact match, but check the id anyway: a loose match must never close someone else's ticket.
        foreach (var ticket in tickets.EnumerateArray())
        {
            if (string.Equals(Text(ticket, "ticketId"), ticketId, StringComparison.Ordinal))
            {
                return new SuperOpsTicket(StatusName(ticket));
            }
        }

        return null;
    }

    public async Task CloseTicketAsync(SuperOpsSettings settings, string ticketId, string note, CancellationToken cancellationToken = default)
    {
        const string noteQuery = """
            mutation CreateTicketNote($input: CreateTicketNoteInput!) {
              createTicketNote(input: $input) { noteId }
            }
            """;
        const string updateQuery = """
            mutation UpdateTicket($input: UpdateTicketInput!) {
              updateTicket(input: $input) { ticketId }
            }
            """;

        // Some accounts won't resolve a ticket that has no category. Try the plain change first so a category a tech set
        // is never overwritten, and fall back to the configured one only when SuperOps says the category is missing.
        var updateInput = new { ticketId, status = settings.ClosedStatus };
        try
        {
            await ExecuteAsync(settings, updateQuery, new { input = updateInput }, "updateTicket", cancellationToken).ConfigureAwait(false);
        }
        catch (SuperOpsApiException ex) when (ex.IsMissingValueFor("category"))
        {
            if (string.IsNullOrWhiteSpace(settings.Category))
            {
                throw new SuperOpsApiException($"{ex.Message} Choose a Category in the SuperOps settings so dotMARC can resolve tickets.", ex.StatusCode, ex.MissingAttributes);
            }

            var withCategory = new { ticketId, status = settings.ClosedStatus, category = settings.Category };
            await ExecuteAsync(settings, updateQuery, new { input = withCategory }, "updateTicket", cancellationToken).ConfigureAwait(false);
        }

        // The note comes after the status change, so a close that keeps failing and being retried doesn't pile up notes.
        // It's a courtesy: the ticket is already closed, so a refused note doesn't fail the close.
        try
        {
            var noteInput = new { ticket = new { ticketId }, content = ToHtml(note), privacyType = "PRIVATE" };
            await ExecuteAsync(settings, noteQuery, new { input = noteInput }, "createTicketNote", cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
        }
    }

    /// <summary>Runs one GraphQL document and returns its "data". When <paramref name="requiredField"/> is given, the
    /// call fails if that field came back null, using the errors to say why; errors on other fields are ignored, which
    /// is how SuperOps reports a field it can't fill in a response that is otherwise good.</summary>
    private async Task<JsonElement> ExecuteAsync(SuperOpsSettings settings, string query, object? variables, string? requiredField, CancellationToken cancellationToken)
    {
        var subdomain = settings.Subdomain ?? throw new InvalidOperationException("SuperOps needs the account's subdomain before dotMARC can call it.");
        var token = await secretStore.GetSecretAsync(SuperOpsSettings.ApiTokenSecretKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The SuperOps API token isn't saved.");

        using var request = new HttpRequestMessage(HttpMethod.Post, SuperOpsRegions.ApiUrl(settings.Region))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query, variables }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("CustomerSubDomain", subdomain);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The HttpClient's own timeout, not the caller stopping: report it as a failed request so callers can retry.
            throw new HttpRequestException("SuperOps didn't answer in time.", ex, HttpStatusCode.RequestTimeout);
        }

        using var _ = response;
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            var advice = status switch
            {
                HttpStatusCode.Unauthorized => "SuperOps refused the API token: check it is current and the subdomain and region are right.",
                HttpStatusCode.Forbidden => "SuperOps refused the request: check the API token's owner is allowed to manage tickets.",
                HttpStatusCode.TooManyRequests => "SuperOps is rate limiting dotMARC. It will try again later.",
                _ => $"SuperOps returned {(int)status}."
            };
            throw new HttpRequestException(advice, null, status);
        }

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("SuperOps answered with something that isn't JSON. Check the subdomain and region, and any proxy in front of dotMARC.", ex, response.StatusCode);
        }

        using var parsed = document;
        var root = document.RootElement;
        var data = root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object ? dataElement.Clone() : (JsonElement?)null;
        var hasRequired = requiredField is null
            ? data is { } available && available.EnumerateObject().Any(property => property.Value.ValueKind != JsonValueKind.Null)
            : data is { } present && present.TryGetProperty(requiredField, out var field) && field.ValueKind == JsonValueKind.Object;
        if (hasRequired)
        {
            return data!.Value;
        }

        throw new SuperOpsApiException(DescribeErrors(root), HttpStatusCode.BadRequest, MissingAttributes(root));
    }

    /// <summary>The attributes SuperOps says are missing, from the mandatory_validation_failed client errors.</summary>
    private static IReadOnlyList<string> MissingAttributes(JsonElement root)
    {
        var missing = new List<string>();
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var error in errors.EnumerateArray())
            {
                if (error.TryGetProperty("extensions", out var extensions) && extensions.ValueKind == JsonValueKind.Object
                    && extensions.TryGetProperty("clientError", out var clientErrors) && clientErrors.ValueKind == JsonValueKind.Array)
                {
                    foreach (var clientError in clientErrors.EnumerateArray())
                    {
                        if (Text(clientError, "code") == "mandatory_validation_failed"
                            && clientError.TryGetProperty("param", out var param) && param.ValueKind == JsonValueKind.Object
                            && param.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Array)
                        {
                            missing.AddRange(attributes.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!));
                        }
                    }
                }
            }
        }

        return missing;
    }

    private static string DescribeErrors(JsonElement root)
    {
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            var messages = errors.EnumerateArray().Select(ErrorText).Where(message => !string.IsNullOrWhiteSpace(message)).Take(3).ToList();
            if (messages.Count > 0)
            {
                // SuperOps's error messages describe the problem and don't echo credentials.
                return Truncate($"SuperOps said: {string.Join("; ", messages)}", 400);
            }
        }

        return "SuperOps returned no data.";
    }

    /// <summary>An error's message, or, when SuperOps sends a null message with the reason under extensions.clientError
    /// (as it does for a missing required field), a sentence built from that.</summary>
    private static string? ErrorText(JsonElement error)
    {
        var message = Text(error, "message");
        if (!string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        if (error.TryGetProperty("extensions", out var extensions)
            && extensions.ValueKind == JsonValueKind.Object
            && extensions.TryGetProperty("clientError", out var clientErrors)
            && clientErrors.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var clientError in clientErrors.EnumerateArray())
            {
                var code = Text(clientError, "code");
                var attributes = clientError.TryGetProperty("param", out var param) && param.ValueKind == JsonValueKind.Object
                    && param.TryGetProperty("attributes", out var list) && list.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", list.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()))
                    : "";
                parts.Add(code == "mandatory_validation_failed" ? $"a value is required for {attributes}." : $"{code} {attributes}".Trim());
            }

            return string.Join("; ", parts.Where(part => part.Length > 0));
        }

        return null;
    }

    private static IReadOnlyList<string> Names(JsonElement data, string field) =>
        data.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(item => Text(item, "name")).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    /// <summary>A ticket's status comes back as a plain name, but accept an object with a name too.</summary>
    private static string? StatusName(JsonElement ticket)
    {
        if (!ticket.TryGetProperty("status", out var status))
        {
            return null;
        }

        return status.ValueKind switch
        {
            JsonValueKind.String => status.GetString(),
            JsonValueKind.Object => Text(status, "name"),
            _ => null
        };
    }

    /// <summary>SuperOps ids are long numbers that can arrive as strings or numbers; keep them as text.</summary>
    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

    private static void AddIfSet(Dictionary<string, object?> input, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            input[name] = value;
        }
    }

    /// <summary>SuperOps shows descriptions and notes as HTML, so plain text keeps its line breaks only as tags.</summary>
    private static string ToHtml(string text) => WebUtility.HtmlEncode(text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "<br/>", StringComparison.Ordinal);

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];
}
