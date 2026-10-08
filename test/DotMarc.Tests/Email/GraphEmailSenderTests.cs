using System.Net;
using System.Text.Json;
using DotMarc.Email;
using DotMarc.Graph;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Email;

public sealed class GraphEmailSenderTests
{
    private sealed class FixedToken : IGraphTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult("token");
    }

    private static (GraphEmailSender Sender, FakeHttpMessageHandler Handler) Create()
    {
        var handler = new FakeHttpMessageHandler { StatusCode = HttpStatusCode.Accepted, ResponseBody = "" };
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        return (new GraphEmailSender(http, new FixedToken(), "reports@nova-msp.example"), handler);
    }

    [Fact]
    public async Task SendMail_IsPostedAsTheMailbox_WithTheAttachmentInline()
    {
        var (sender, handler) = Create();

        await sender.SendAsync(new EmailMessage(["it@aurora-retail.example"], "Subject", "<p>Hi</p>", "Hi",
            [new EmailAttachment("report.pdf", "application/pdf", [1, 2, 3])]), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://graph.microsoft.com/v1.0/users/reports@nova-msp.example/sendMail", request.RequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.RequestBodies.Single());
        var message = body.RootElement.GetProperty("message");
        Assert.Equal("it@aurora-retail.example", message.GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.Equal("HTML", message.GetProperty("body").GetProperty("contentType").GetString());
        var attachment = message.GetProperty("attachments")[0];
        Assert.Equal(("#microsoft.graph.fileAttachment", "report.pdf", "AQID"),
            (attachment.GetProperty("@odata.type").GetString(), attachment.GetProperty("name").GetString(), attachment.GetProperty("contentBytes").GetString()));
        Assert.False(body.RootElement.GetProperty("saveToSentItems").GetBoolean());
    }

    [Fact]
    public async Task AGraphError_IsAnEmailSendException_CarryingGraphsMessage()
    {
        var (sender, handler) = Create();
        handler.StatusCode = HttpStatusCode.Forbidden;
        handler.ResponseBody = """{"error":{"code":"ErrorAccessDenied","message":"Access is denied. Check credentials and try again."}}""";

        var exception = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(
            new EmailMessage(["it@aurora-retail.example"], "Subject", "<p>Hi</p>", "Hi", []), CancellationToken.None));

        Assert.Contains("Access is denied", exception.Message);
        Assert.Contains("Mail.Send", exception.Message);
    }
}
