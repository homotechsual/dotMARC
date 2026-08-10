using DotMarc.Graph;
using DotMarc.Tests.Internal;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotMarc.Tests.Graph;

public class GraphMailboxClientTests
{
    private sealed class FakeGraphTokenProvider : IGraphTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult("fake-token");
    }

    private static (GraphMailboxClient client, FakeHttpMessageHandler handler) CreateClient()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        var options = Options.Create(new GraphOptions
        {
            ClientId = "client-id",
            TenantId = "tenant-id",
            ClientSecret = "secret",
            MailboxAddress = "dmarc-reports@example.com"
        });
        return (new GraphMailboxClient(http, options, new FakeGraphTokenProvider()), handler);
    }

    [Fact]
    public async Task GetUnreadMessagesAsync_ParsesMessageListResponse()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """
            {"value":[{"id":"msg-1","subject":"Report domain: contoso.io","hasAttachments":true}]}
            """;

        var result = await client.GetUnreadMessagesAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("msg-1", result[0].Id);
        Assert.True(result[0].HasAttachments);
        Assert.Contains("users/dmarc-reports@example.com/messages", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("isRead eq false", Uri.UnescapeDataString(handler.Requests[0].RequestUri!.ToString()));
        Assert.Contains("$top=50", Uri.UnescapeDataString(handler.Requests[0].RequestUri!.ToString()));
    }

    [Fact]
    public async Task GetUnreadMessagesAsync_FollowsODataNextLink_AcrossMultiplePages()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""
            {"value":[{"id":"msg-1","subject":"Page 1","hasAttachments":true}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users/dmarc-reports@example.com/messages?$skiptoken=abc"}
            """);
        handler.ResponseBodies.Enqueue("""
            {"value":[{"id":"msg-2","subject":"Page 2","hasAttachments":false},{"id":"msg-3","subject":"Page 2b","hasAttachments":true}]}
            """);

        var result = await client.GetUnreadMessagesAsync(CancellationToken.None);

        Assert.Equal(3, result.Count);
        Assert.Equal(["msg-1", "msg-2", "msg-3"], result.Select(m => m.Id));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("$top=50", Uri.UnescapeDataString(handler.Requests[0].RequestUri!.ToString()));
        Assert.Equal(
            "https://graph.microsoft.com/v1.0/users/dmarc-reports@example.com/messages?$skiptoken=abc",
            handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task GetAttachmentsAsync_ParsesAttachmentListResponse_DecodingBase64Content()
    {
        var (client, handler) = CreateClient();
        var base64 = Convert.ToBase64String("<feedback/>"u8.ToArray());
        handler.ResponseBody = $$"""
            {"value":[{"name":"report.xml","contentType":"text/xml","contentBytes":"{{base64}}"}]}
            """;

        var result = await client.GetAttachmentsAsync("msg-1", CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("report.xml", result[0].Name);
        Assert.Equal("<feedback/>", System.Text.Encoding.UTF8.GetString(result[0].ContentBytes));
        Assert.Contains("messages/msg-1/attachments", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task MarkAsReadAsync_SendsPatchWithIsReadTrue()
    {
        var (client, handler) = CreateClient();

        await client.MarkAsReadAsync("msg-1", CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, handler.Requests[0].Method);
        Assert.Contains("messages/msg-1", handler.Requests[0].RequestUri!.ToString());
        var body = await handler.Requests[0].Content!.ReadAsStringAsync();
        Assert.Contains("\"isRead\":true", body);
    }
}
