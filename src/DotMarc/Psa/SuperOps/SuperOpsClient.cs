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
        for (var page = 1; ; page++)
        {
            var data = await ExecuteAsync(settings, query, new { input = new { page, pageSize = PageSize } }, "getClientList", cancellationToken).ConfigureAwait(false);
            var list = data.GetProperty("getClientList");
            if (list.TryGetProperty("clients", out var clients) && clients.ValueKind == JsonValueKind.Array)
            {
                companies.AddRange(clients.EnumerateArray()
                    .Select(client => new PsaCompany(Text(client, "accountId") ?? string.Empty, Text(client, "name") ?? string.Empty))
                    .Where(company => company.Id.Length > 0 && company.Name.Length > 0));
            }

            if (!(list.TryGetProperty("listInfo", out var info) && info.ValueKind == JsonValueKind.Object
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
        return new SuperOpsPicklists(Names(data, "getStatusList"), Names(data, "getPriorityList"), Names(data, "getUrgencyList"), Names(data, "getCategoryList"));
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
            return null;
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

        // The note first, so the ticket says why it closed even if the status change is refused.
        var noteInput = new { ticket = new { ticketId }, content = ToHtml(note), privacyType = "PRIVATE" };
        await ExecuteAsync(settings, noteQuery, new { input = noteInput }, "createTicketNote", cancellationToken).ConfigureAwait(false);

        // Some accounts won't resolve a ticket that has no category. Try the plain change first so a category a tech set
        // is never overwritten, and fall back to the configured one only when SuperOps asks for it.
        var updateInput = new { ticketId, status = settings.ClosedStatus };
        try
        {
            await ExecuteAsync(settings, updateQuery, new { input = updateInput }, "updateTicket", cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("category", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(settings.Category))
            {
                throw new HttpRequestException($"{ex.Message} Choose a Category in the SuperOps settings so dotMARC can resolve tickets.", ex, ex.StatusCode);
            }

            var withCategory = new { ticketId, status = settings.ClosedStatus, category = settings.Category };
            await ExecuteAsync(settings, updateQuery, new { input = withCategory }, "updateTicket", cancellationToken).ConfigureAwait(false);
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

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
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

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var data = root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object ? dataElement.Clone() : (JsonElement?)null;
        var hasRequired = requiredField is null
            ? data is { } available && available.EnumerateObject().Any(property => property.Value.ValueKind != JsonValueKind.Null)
            : data is { } present && present.TryGetProperty(requiredField, out var field) && field.ValueKind == JsonValueKind.Object;
        if (hasRequired)
        {
            return data!.Value;
        }

        throw new HttpRequestException(DescribeErrors(root), null, HttpStatusCode.BadRequest);
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
