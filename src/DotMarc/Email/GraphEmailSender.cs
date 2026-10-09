using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DotMarc.Graph;

namespace DotMarc.Email;

/// <summary>Sends through Microsoft Graph as a mailbox in the tenant. A message that fits in one request (Graph caps
/// requests at 4 MB, and attachments grow by a third when encoded) goes in a single sendMail call, which needs Mail.Send.
/// A larger one is created as a draft, its attachments uploaded in chunks through upload sessions, and the draft sent;
/// creating a draft also needs Mail.ReadWrite.</summary>
public sealed class GraphEmailSender(HttpClient http, IGraphTokenProvider tokenProvider, string mailbox) : IEmailSender
{
    public const string HttpClientName = "GraphEmail";

    private const int MaximumRequestBytes = 4 * 1024 * 1024;

    /// <summary>Upload chunks must be a multiple of 320 KiB; ten of them stays well under the 4 MB request cap.</summary>
    internal const int UploadChunkBytes = 10 * 320 * 1024;

    private string MailboxPath => $"users/{Uri.EscapeDataString(mailbox).Replace("%40", "@")}";

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        EmailLimits.Check(message);

        var json = JsonSerializer.Serialize(new { message = MessageBody(message, includeAttachments: true), saveToSentItems = false });
        if (Encoding.UTF8.GetByteCount(json) <= MaximumRequestBytes)
        {
            using var sent = await GraphAsync(HttpMethod.Post, $"{MailboxPath}/sendMail", Json(json), "", cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendLargeAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendLargeAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        const string draftHint = " Sending a report over 4 MB creates it as a draft first, which needs the Mail.ReadWrite application permission as well as Mail.Send.";
        using var created = await GraphAsync(HttpMethod.Post, $"{MailboxPath}/messages",
            Json(JsonSerializer.Serialize(MessageBody(message, includeAttachments: false))), draftHint, cancellationToken).ConfigureAwait(false);
        var draftId = ReadString(await created.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), "id")
            ?? throw new EmailSendException("Microsoft Graph created the draft but didn't say its id.");
        var draftPath = $"{MailboxPath}/messages/{Uri.EscapeDataString(draftId)}";

        try
        {
            foreach (var attachment in message.Attachments)
            {
                await UploadAsync(draftPath, attachment, cancellationToken).ConfigureAwait(false);
            }

            using var sent = await GraphAsync(HttpMethod.Post, $"{draftPath}/send", content: null, "", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Don't leave a half-built report sitting in the mailbox's drafts.
            try
            {
                using var deleted = await GraphAsync(HttpMethod.Delete, draftPath, content: null, "", CancellationToken.None).ConfigureAwait(false);
            }
            catch (EmailSendException)
            {
            }

            throw exception as EmailSendException ?? new EmailSendException($"Sending through Microsoft Graph failed: {exception.Message}", exception);
        }
    }

    private async Task UploadAsync(string draftPath, EmailAttachment attachment, CancellationToken cancellationToken)
    {
        var sessionRequest = JsonSerializer.Serialize(new
        {
            AttachmentItem = new { attachmentType = "file", name = attachment.FileName, size = attachment.Bytes.Length, contentType = attachment.ContentType },
        });
        using var session = await GraphAsync(HttpMethod.Post, $"{draftPath}/attachments/createUploadSession", Json(sessionRequest), "", cancellationToken).ConfigureAwait(false);
        var uploadUrl = ReadString(await session.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), "uploadUrl")
            ?? throw new EmailSendException("Microsoft Graph didn't return an upload address for the attachment.");

        var total = attachment.Bytes.Length;
        for (var start = 0; start < total; start += UploadChunkBytes)
        {
            var length = Math.Min(UploadChunkBytes, total - start);
            var chunk = new ByteArrayContent(attachment.Bytes, start, length);
            chunk.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            chunk.Headers.ContentRange = new ContentRangeHeaderValue(start, start + length - 1, total);

            // The upload address carries its own authorisation; Graph refuses an upload that also has a bearer token.
            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = chunk };
            using var response = await SendRawAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "", cancellationToken).ConfigureAwait(false);
        }
    }

    private static Dictionary<string, object> MessageBody(EmailMessage message, bool includeAttachments)
    {
        var body = new Dictionary<string, object>
        {
            ["subject"] = message.Subject,
            ["body"] = new { contentType = "HTML", content = message.HtmlBody },
            ["toRecipients"] = message.To.Select(address => new { emailAddress = new { address } }).ToList(),
        };
        if (includeAttachments)
        {
            body["attachments"] = message.Attachments.Select(attachment => new Dictionary<string, object>
            {
                ["@odata.type"] = "#microsoft.graph.fileAttachment",
                ["name"] = attachment.FileName,
                ["contentType"] = attachment.ContentType,
                ["contentBytes"] = Convert.ToBase64String(attachment.Bytes),
            }).ToList();
        }

        return body;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>A Graph call with the app's token; throws <see cref="EmailSendException"/> with Graph's message (and
    /// <paramref name="forbiddenHint"/>, or the general Mail.Send hint, on a 403).</summary>
    private async Task<HttpResponseMessage> GraphAsync(HttpMethod method, string path, HttpContent? content, string forbiddenHint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        try
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new EmailSendException($"Couldn't sign in to Microsoft Graph: {exception.Message}", exception);
        }

        var response = await SendRawAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, forbiddenHint, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new EmailSendException($"Couldn't reach Microsoft Graph: {exception.Message}", exception);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string forbiddenHint, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.Dispose();
        var graphMessage = TryReadGraphError(body) ?? response.ReasonPhrase ?? "no details";
        var hint = response.StatusCode == System.Net.HttpStatusCode.Forbidden
            ? forbiddenHint.Length > 0
                ? forbiddenHint
                : " Check the app registration has the Mail.Send application permission with admin consent, and that it may send as this mailbox."
            : "";
        throw new EmailSendException($"Microsoft Graph refused the message ({(int)response.StatusCode}): {graphMessage}{hint}");
    }

    private static string? ReadString(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(property, out var value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadGraphError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("error").GetProperty("message").GetString();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
