using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DotMarc.Graph;

namespace DotMarc.Email;

/// <summary>Sends through Microsoft Graph as a mailbox in the tenant (needs the Mail.Send application permission).
/// The attachment goes inline with the message, which Graph accepts up to 3 MB.</summary>
public sealed class GraphEmailSender(HttpClient http, IGraphTokenProvider tokenProvider, string mailbox) : IEmailSender
{
    public const string HttpClientName = "GraphEmail";

    /// <summary>Graph refuses a sendMail request over 4 MB, and attachments grow by a third when base64-encoded.</summary>
    private const int MaximumRequestBytes = 4 * 1024 * 1024;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        EmailLimits.Check(message);

        var payload = new
        {
            message = new Dictionary<string, object>
            {
                ["subject"] = message.Subject,
                ["body"] = new { contentType = "HTML", content = message.HtmlBody },
                ["toRecipients"] = message.To.Select(address => new { emailAddress = new { address } }).ToList(),
                ["attachments"] = message.Attachments.Select(attachment => new Dictionary<string, object>
                {
                    ["@odata.type"] = "#microsoft.graph.fileAttachment",
                    ["name"] = attachment.FileName,
                    ["contentType"] = attachment.ContentType,
                    ["contentBytes"] = Convert.ToBase64String(attachment.Bytes),
                }).ToList(),
            },
            saveToSentItems = false,
        };

        var json = JsonSerializer.Serialize(payload);
        if (Encoding.UTF8.GetByteCount(json) > MaximumRequestBytes)
        {
            throw new EmailSendException("The message is too large to send through Microsoft Graph (over 4 MB once encoded).");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"users/{Uri.EscapeDataString(mailbox).Replace("%40", "@")}/sendMail")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        HttpResponseMessage response;
        try
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false));
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new EmailSendException($"Couldn't reach Microsoft Graph: {exception.Message}", exception);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var graphMessage = TryReadGraphError(body) ?? response.ReasonPhrase ?? "no details";
            var hint = response.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? " Check the app registration has the Mail.Send application permission with admin consent, and that it may send as this mailbox."
                : "";
            throw new EmailSendException($"Microsoft Graph refused the message ({(int)response.StatusCode}): {graphMessage}{hint}");
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
