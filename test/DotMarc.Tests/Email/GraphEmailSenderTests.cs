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
        public int Invalidations { get; private set; }

        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult("token");

        public void Invalidate() => Invalidations++;
    }

    private static (GraphEmailSender Sender, FakeHttpMessageHandler Handler) Create() => Create(new FixedToken());

    private static (GraphEmailSender Sender, FakeHttpMessageHandler Handler) Create(FixedToken token)
    {
        var handler = new FakeHttpMessageHandler { StatusCode = HttpStatusCode.Accepted, ResponseBody = "" };
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        return (new GraphEmailSender(http, token, "reports@nova-msp.example"), handler);
    }

    [Fact]
    public async Task ARefusalWithAStaleToken_IsRetriedOnce_WithAFreshToken()
    {
        var token = new FixedToken();
        var (sender, handler) = Create(token);
        handler.StatusCodes.Enqueue(HttpStatusCode.Forbidden);
        handler.ResponseBodies.Enqueue("""{"error":{"code":"ErrorAccessDenied","message":"Access is denied."}}""");
        handler.StatusCodes.Enqueue(HttpStatusCode.Accepted);
        handler.ResponseBodies.Enqueue("");

        await sender.SendAsync(new EmailMessage(["it@aurora-retail.example"], "Subject", "<p>Hi</p>", "Hi", []), CancellationToken.None);

        Assert.Equal((2, 1), (handler.Requests.Count, token.Invalidations));
    }

    [Fact]
    public async Task ARefusalThatPersists_WithAFreshToken_IsReported()
    {
        var token = new FixedToken();
        var (sender, handler) = Create(token);
        handler.StatusCode = HttpStatusCode.Forbidden;
        handler.ResponseBody = """{"error":{"code":"ErrorAccessDenied","message":"Access is denied."}}""";

        await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(
            new EmailMessage(["it@aurora-retail.example"], "Subject", "<p>Hi</p>", "Hi", []), CancellationToken.None));

        Assert.Equal((2, 1), (handler.Requests.Count, token.Invalidations));
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

    private static EmailMessage LargeMessage() => new(["it@aurora-retail.example"], "Subject", "<p>Hi</p>", "Hi",
        [new EmailAttachment("report.pdf", "application/pdf", new byte[4 * 1024 * 1024])]); // over Graph's 4 MB request cap once encoded

    [Fact]
    public async Task ALargeMessage_IsSentAsADraft_WithTheAttachmentUploadedInChunks()
    {
        var (sender, handler) = Create();
        foreach (var (status, body) in new[]
        {
            (HttpStatusCode.Created, """{"id":"draft-1"}"""),
            (HttpStatusCode.Created, """{"uploadUrl":"https://upload.example/session-1"}"""),
            (HttpStatusCode.OK, """{"nextExpectedRanges":["3276800-"]}"""),
            (HttpStatusCode.Created, "{}"),
            (HttpStatusCode.Accepted, ""),
        })
        {
            handler.StatusCodes.Enqueue(status);
            handler.ResponseBodies.Enqueue(body);
        }

        await sender.SendAsync(LargeMessage(), CancellationToken.None);

        Assert.Equal(
        [
            "POST https://graph.microsoft.com/v1.0/users/reports@nova-msp.example/messages",
            "POST https://graph.microsoft.com/v1.0/users/reports@nova-msp.example/messages/draft-1/attachments/createUploadSession",
            "PUT https://upload.example/session-1",
            "PUT https://upload.example/session-1",
            "POST https://graph.microsoft.com/v1.0/users/reports@nova-msp.example/messages/draft-1/send",
        ], handler.Requests.Select(request => $"{request.Method} {request.RequestUri}"));
        Assert.Equal(["bytes 0-3276799/4194304", "bytes 3276800-4194303/4194304"],
            handler.Requests.Where(request => request.Method == HttpMethod.Put).Select(request => request.Content!.Headers.ContentRange!.ToString()));
        // The upload URL carries its own authorisation; Graph refuses one with a bearer token as well.
        Assert.All(handler.Requests.Where(request => request.Method == HttpMethod.Put), request => Assert.Null(request.Headers.Authorization));
        using var draft = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.False(draft.RootElement.TryGetProperty("attachments", out _));
        using var session = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal(("file", "report.pdf", 4194304),
            (session.RootElement.GetProperty("AttachmentItem").GetProperty("attachmentType").GetString(),
             session.RootElement.GetProperty("AttachmentItem").GetProperty("name").GetString(),
             session.RootElement.GetProperty("AttachmentItem").GetProperty("size").GetInt32()));
    }

    [Fact]
    public async Task ALargeMessage_WithoutMailReadWrite_SaysWhichPermissionIsMissing()
    {
        var (sender, handler) = Create();
        handler.StatusCode = HttpStatusCode.Forbidden;
        handler.ResponseBody = """{"error":{"code":"ErrorAccessDenied","message":"Access is denied."}}""";

        var exception = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(LargeMessage(), CancellationToken.None));

        Assert.Contains("Mail.ReadWrite", exception.Message);
        Assert.Equal(2, handler.Requests.Count); // the draft, then once more with a fresh token
    }

    [Fact]
    public async Task AFailedUpload_DeletesTheDraft()
    {
        var (sender, handler) = Create();
        foreach (var (status, body) in new[]
        {
            (HttpStatusCode.Created, """{"id":"draft-1"}"""),
            (HttpStatusCode.Created, """{"uploadUrl":"https://upload.example/session-1"}"""),
            (HttpStatusCode.InternalServerError, """{"error":{"code":"generalException","message":"Upload failed."}}"""),
            (HttpStatusCode.NoContent, ""),
        })
        {
            handler.StatusCodes.Enqueue(status);
            handler.ResponseBodies.Enqueue(body);
        }

        await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(LargeMessage(), CancellationToken.None));

        Assert.Equal("DELETE https://graph.microsoft.com/v1.0/users/reports@nova-msp.example/messages/draft-1",
            $"{handler.Requests[^1].Method} {handler.Requests[^1].RequestUri}");
    }

    [Fact]
    public async Task AnApplicationAccessPolicyRefusal_IsNamed_RatherThanBlamedOnMailSend()
    {
        var (sender, handler) = Create();
        handler.StatusCode = HttpStatusCode.Forbidden;
        handler.ResponseBody = """{"error":{"code":"ErrorAccessDenied","message":"Access to OData is disabled: [RAOP] : Blocked by tenant configured AppOnly AccessPolicy settings."}}""";

        var exception = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(
            new EmailMessage(["it@aurora-retail.example"], "Subject", "<p>Hi</p>", "Hi", []), CancellationToken.None));

        Assert.Contains("application access policy", exception.Message);
        Assert.Contains("Test-ApplicationAccessPolicy", exception.Message);
        Assert.Contains("reports@nova-msp.example", exception.Message);
        Assert.DoesNotContain("Mail.Send application permission", exception.Message);
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
