using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotMarc.Notifications;

namespace DotMarc.Psa.ConnectWise;

/// <summary>ConnectWise PSA (Manage) REST API, version 3.0. Every call signs in with Basic auth as
/// "company+publicKey:privateKey" and carries the developer clientId header ConnectWise requires.</summary>
public sealed class ConnectWiseClient(HttpClient httpClient, ISecretStore secretStore) : IConnectWiseClient
{
    private const int PageSize = 1000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(ConnectWiseSettings settings, CancellationToken cancellationToken = default)
    {
        var companies = new List<PsaCompany>();
        for (var page = 1; ; page++)
        {
            var entries = await GetListAsync<IdName>(settings, $"company/companies?conditions={Uri.EscapeDataString("deletedFlag=false")}&fields=id,name&pageSize={PageSize}&page={page}", cancellationToken).ConfigureAwait(false);
            companies.AddRange(entries.Select(entry => new PsaCompany(entry.Id.ToString(CultureInfo.InvariantCulture), entry.Name)));

            // A short page is the last one.
            if (entries.Count < PageSize)
            {
                break;
            }
        }

        return companies.OrderBy(company => company.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task<IReadOnlyList<PsaOption>> ListBoardsAsync(ConnectWiseSettings settings, CancellationToken cancellationToken = default) =>
        ListOptionsAsync(settings, $"service/boards?conditions={Uri.EscapeDataString("inactiveFlag=false")}&pageSize={PageSize}", cancellationToken);

    public Task<IReadOnlyList<PsaOption>> ListBoardStatusesAsync(ConnectWiseSettings settings, int boardId, CancellationToken cancellationToken = default) =>
        ListOptionsAsync(settings, $"service/boards/{boardId}/statuses?pageSize={PageSize}", cancellationToken);

    public Task<IReadOnlyList<PsaOption>> ListBoardTypesAsync(ConnectWiseSettings settings, int boardId, CancellationToken cancellationToken = default) =>
        ListOptionsAsync(settings, $"service/boards/{boardId}/types?pageSize={PageSize}", cancellationToken);

    public Task<IReadOnlyList<PsaOption>> ListPrioritiesAsync(ConnectWiseSettings settings, CancellationToken cancellationToken = default) =>
        ListOptionsAsync(settings, $"service/priorities?pageSize={PageSize}", cancellationToken);

    public async Task<string> CreateTicketAsync(ConnectWiseSettings settings, PsaTicketRequest request, CancellationToken cancellationToken = default)
    {
        var ticket = new CreateTicketBody(
            Truncate(request.Title, 100),
            request.Body,
            new Reference(int.Parse(request.CompanyId, CultureInfo.InvariantCulture)),
            new Reference(settings.BoardId!.Value),
            new Reference(settings.StatusId!.Value),
            settings.TypeId is { } typeId ? new Reference(typeId) : null,
            new Reference(settings.PriorityId!.Value));
        using var response = await SendAsync(settings, HttpMethod.Post, "service/tickets", JsonContent.Create(ticket, options: JsonOptions), cancellationToken).ConfigureAwait(false);
        var created = await response.Content.ReadFromJsonAsync<IdOnly>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("ConnectWise created a ticket but didn't say its id.");
        return created.Id.ToString(CultureInfo.InvariantCulture);
    }

    public async Task<ConnectWiseTicket?> GetTicketAsync(ConnectWiseSettings settings, string ticketId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(settings, HttpMethod.Get, $"service/tickets/{Uri.EscapeDataString(ticketId)}?fields=id,closedFlag,status", null, cancellationToken).ConfigureAwait(false);
            var ticket = await response.Content.ReadFromJsonAsync<TicketState>(JsonOptions, cancellationToken).ConfigureAwait(false);
            return ticket is null ? null : new ConnectWiseTicket(ticket.ClosedFlag, ticket.Status?.Id);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task CloseTicketAsync(ConnectWiseSettings settings, string ticketId, string note, CancellationToken cancellationToken = default)
    {
        var escapedId = Uri.EscapeDataString(ticketId);

        // The note first, so the ticket says why it closed even if the status change is refused.
        using (await SendAsync(settings, HttpMethod.Post, $"service/tickets/{escapedId}/notes", JsonContent.Create(new NoteBody(note, true), options: JsonOptions), cancellationToken).ConfigureAwait(false))
        {
        }

        PatchOperation[] patch = [new("replace", "status", new Reference(settings.ClosedStatusId!.Value))];
        using (await SendAsync(settings, HttpMethod.Patch, $"service/tickets/{escapedId}", JsonContent.Create(patch, options: JsonOptions), cancellationToken).ConfigureAwait(false))
        {
        }
    }

    private async Task<IReadOnlyList<PsaOption>> ListOptionsAsync(ConnectWiseSettings settings, string path, CancellationToken cancellationToken)
    {
        var entries = await GetListAsync<IdName>(settings, path, cancellationToken).ConfigureAwait(false);
        return entries.Select(entry => new PsaOption(entry.Id, entry.Name)).OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<List<T>> GetListAsync<T>(ConnectWiseSettings settings, string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(settings, HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<List<T>>(JsonOptions, cancellationToken).ConfigureAwait(false) ?? [];
    }

    private async Task<HttpResponseMessage> SendAsync(ConnectWiseSettings settings, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        var clientId = settings.EffectiveClientId ?? throw new InvalidOperationException("ConnectWise needs a client ID before dotMARC can call it.");
        var privateKey = await secretStore.GetSecretAsync(ConnectWiseSettings.PrivateKeySecretKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The ConnectWise private key isn't saved.");

        using var request = new HttpRequestMessage(method, $"https://{settings.SiteUrl}/v4_6_release/apis/3.0/{path}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.CompanyId}+{settings.PublicKey}:{privateKey}")));
        request.Headers.Add("clientId", clientId);
        var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var status = response.StatusCode;
        response.Dispose();
        var advice = status switch
        {
            HttpStatusCode.Unauthorized => "ConnectWise refused the sign-in: check the company ID, public key and private key.",
            HttpStatusCode.Forbidden => "ConnectWise refused the request: check the API member's security role allows it.",
            _ => $"ConnectWise returned {(int)status} for {method} {path.Split('?')[0]}."
        };

        // ConnectWise's error bodies describe the problem and don't echo credentials.
        throw new HttpRequestException(string.IsNullOrWhiteSpace(body) ? advice : $"{advice} ConnectWise said: {Truncate(body, 300)}", null, status);
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    private sealed record IdName(int Id, string Name);
    private sealed record IdOnly(int Id);
    private sealed record Reference(int Id);
    private sealed record TicketState(int Id, bool ClosedFlag, Reference? Status);
    private sealed record NoteBody(string Text, bool InternalAnalysisFlag);
    private sealed record PatchOperation(string Op, string Path, Reference Value);
    private sealed record CreateTicketBody(
        string Summary,
        string InitialDescription,
        Reference Company,
        Reference Board,
        Reference Status,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Reference? Type,
        Reference Priority);
}
