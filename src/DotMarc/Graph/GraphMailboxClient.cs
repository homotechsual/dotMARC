using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace DotMarc.Graph;

public sealed class GraphMailboxClient : IGraphMailboxClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly GraphOptions _options;
    private readonly IGraphTokenProvider _tokenProvider;

    public GraphMailboxClient(HttpClient http, IOptions<GraphOptions> options, IGraphTokenProvider tokenProvider)
    {
        _http = http;
        _options = options.Value;
        _tokenProvider = tokenProvider;
    }

    public async Task<IReadOnlyList<MailboxMessage>> GetUnreadMessagesAsync(CancellationToken cancellationToken)
    {
        var messages = new List<MailboxMessage>();
        string? path = $"users/{_options.MailboxAddress}/messages?$filter=isRead eq false&$select=id,subject,hasAttachments&$top=50";

        // Graph defaults to a page size of 10 for this endpoint; without following
        // @odata.nextLink, anything past the first page is silently dropped. $top=50 shrinks the
        // number of round trips for the common case, but an inbox can still exceed that in one
        // poll cycle, so every page is followed until the server stops returning a nextLink.
        while (path is not null)
        {
            var response = await SendAsync(HttpMethod.Get, path, content: null, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<MessageListResponse>(body, JsonOptions)!;
            messages.AddRange(parsed.Value.Select(m => new MailboxMessage(m.Id, m.Subject, m.HasAttachments)));
            path = parsed.NextLink;
        }

        return messages;
    }

    public async Task<IReadOnlyList<MailboxAttachment>> GetAttachmentsAsync(string messageId, CancellationToken cancellationToken)
    {
        var path = $"users/{_options.MailboxAddress}/messages/{messageId}/attachments";
        var response = await SendAsync(HttpMethod.Get, path, content: null, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<AttachmentListResponse>(body, JsonOptions)!;
        return parsed.Value.Select(a => new MailboxAttachment(
            a.Name, a.ContentType, Convert.FromBase64String(a.ContentBytes))).ToList();
    }

    public async Task MarkAsReadAsync(string messageId, CancellationToken cancellationToken)
    {
        var path = $"users/{_options.MailboxAddress}/messages/{messageId}";
        var content = new StringContent("{\"isRead\":true}", System.Text.Encoding.UTF8, "application/json");
        await SendAsync(HttpMethod.Patch, path, content, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        var token = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private sealed record MessageListResponse(
        [property: JsonPropertyName("value")] List<MessageDto> Value,
        [property: JsonPropertyName("@odata.nextLink")] string? NextLink = null);
    private sealed record MessageDto(string Id, string Subject, bool HasAttachments);

    private sealed record AttachmentListResponse([property: JsonPropertyName("value")] List<AttachmentDto> Value);
    private sealed record AttachmentDto(string Name, string ContentType, string ContentBytes);
}
