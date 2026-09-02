using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace DotMarc.Graph;

public sealed class TlsrptGraphMailboxClient : ITlsrptGraphMailboxClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly string _mailboxAddress;
    private readonly IGraphTokenProvider _tokenProvider;

    public TlsrptGraphMailboxClient(HttpClient http, IOptions<GraphOptions> options, IGraphTokenProvider tokenProvider)
    {
        _http = http;
        _mailboxAddress = options.Value.TlsrptMailboxAddress ?? throw new InvalidOperationException("Graph:TlsrptMailboxAddress is required for TLSRPT mailbox access.");
        _tokenProvider = tokenProvider;
    }

    public async Task<IReadOnlyList<MailboxMessage>> GetUnreadMessagesAsync(CancellationToken cancellationToken)
    {
        var messages = new List<MailboxMessage>();
        string? path = $"users/{_mailboxAddress}/messages?$filter=isRead eq false&$select=id,subject,hasAttachments&$top=50";
        while (path is not null)
        {
            using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<MessageListResponse>(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), JsonOptions)!;
            messages.AddRange(parsed.Value.Select(message => new MailboxMessage(message.Id, message.Subject, message.HasAttachments)));
            path = parsed.NextLink;
        }
        return messages;
    }

    public async Task<IReadOnlyList<MailboxAttachment>> GetAttachmentsAsync(string messageId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"users/{_mailboxAddress}/messages/{messageId}/attachments", null, cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<AttachmentListResponse>(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), JsonOptions)!;
        return parsed.Value.Select(attachment => new MailboxAttachment(attachment.Name, attachment.ContentType, Convert.FromBase64String(attachment.ContentBytes))).ToList();
    }

    public async Task MarkAsReadAsync(string messageId, CancellationToken cancellationToken)
    {
        using var content = new StringContent("{\"isRead\":true}", System.Text.Encoding.UTF8, "application/json");
        using var response = await SendAsync(HttpMethod.Patch, $"users/{_mailboxAddress}/messages/{messageId}", content, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false));
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private sealed record MessageListResponse([property: JsonPropertyName("value")] List<MessageDto> Value, [property: JsonPropertyName("@odata.nextLink")] string? NextLink);
    private sealed record MessageDto(string Id, string Subject, bool HasAttachments);
    private sealed record AttachmentListResponse([property: JsonPropertyName("value")] List<AttachmentDto> Value);
    private sealed record AttachmentDto(string Name, string ContentType, string ContentBytes);
}