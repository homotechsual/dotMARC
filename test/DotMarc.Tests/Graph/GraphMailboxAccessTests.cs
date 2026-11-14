using System.Net;
using DotMarc.Graph;
using DotMarc.Tests.Internal;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotMarc.Tests.Graph;

/// <summary>Both mailbox clients turn Graph's bare 401, 403 and mailbox 404 into what's actually wrong.</summary>
public sealed class GraphMailboxAccessTests
{
    private const string AccessDenied = """{"error":{"code":"ErrorAccessDenied","message":"Access is denied. Check credentials and try again."}}""";

    private sealed class FixedTokenProvider(string token) : IGraphTokenProvider
    {
        public int Invalidations { get; private set; }
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult(token);
        public void Invalidate() => Invalidations++;
    }

    private static IOptions<GraphOptions> Options() => Microsoft.Extensions.Options.Options.Create(new GraphOptions
    {
        ClientId = "client-id", TenantId = "tenant-id", ClientSecret = "secret",
        MailboxAddress = "dmarc-reports@example.com", TlsrptMailboxAddress = "tlsrpt@example.com",
    });

    private FixedTokenProvider _tokens = new("");

    private (GraphMailboxClient Dmarc, TlsrptGraphMailboxClient Tlsrpt, FakeHttpMessageHandler Handler) Create(params string[] roles)
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        _tokens = new FixedTokenProvider(GraphAccessProblemTests.TokenWithRoles(roles));
        return (new GraphMailboxClient(http, Options(), _tokens), new TlsrptGraphMailboxClient(http, Options(), _tokens), handler);
    }

    [Fact]
    public async Task TheDmarcMailbox_WithoutAdminConsent_SaysSo()
    {
        var (client, _, handler) = Create();
        (handler.StatusCode, handler.ResponseBody) = (HttpStatusCode.Forbidden, AccessDenied);

        var exception = await Assert.ThrowsAsync<GraphAccessException>(() => client.GetUnreadMessagesAsync(CancellationToken.None));

        Assert.Contains("admin consent", exception.Message);
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
    }

    [Fact]
    public async Task ARefusal_DropsTheCachedToken_SoConsentTakesEffectOnTheNextPoll()
    {
        var (client, _, handler) = Create();
        (handler.StatusCode, handler.ResponseBody) = (HttpStatusCode.Forbidden, AccessDenied);

        await Assert.ThrowsAsync<GraphAccessException>(() => client.GetUnreadMessagesAsync(CancellationToken.None));

        Assert.Equal(1, _tokens.Invalidations);
    }

    [Fact]
    public async Task TheTlsrptMailbox_WithoutAdminConsent_SaysSo()
    {
        var (_, client, handler) = Create();
        (handler.StatusCode, handler.ResponseBody) = (HttpStatusCode.Forbidden, AccessDenied);

        var exception = await Assert.ThrowsAsync<GraphAccessException>(() => client.GetUnreadMessagesAsync(CancellationToken.None));

        Assert.Contains("admin consent", exception.Message);
    }

    [Fact]
    public async Task MarkingRead_WithReadOnlyPermission_AsksForMailReadWrite()
    {
        var (client, _, handler) = Create("Mail.Read");
        (handler.StatusCode, handler.ResponseBody) = (HttpStatusCode.Forbidden, AccessDenied);

        var exception = await Assert.ThrowsAsync<GraphAccessException>(() => client.MarkAsReadAsync("msg-1", CancellationToken.None));

        Assert.Contains("Mail.ReadWrite", exception.Message);
    }

    [Fact]
    public async Task TheTlsrptMailbox_ThatDoesNotExist_IsNamed()
    {
        var (_, client, handler) = Create("Mail.ReadWrite");
        (handler.StatusCode, handler.ResponseBody) = (HttpStatusCode.NotFound, """{"error":{"code":"ErrorInvalidUser","message":"The requested user 'tlsrpt@example.com' is invalid."}}""");

        var exception = await Assert.ThrowsAsync<GraphAccessException>(() => client.GetUnreadMessagesAsync(CancellationToken.None));

        Assert.Contains("tlsrpt@example.com", exception.Message);
    }

    [Fact]
    public async Task AServerError_IsStillAPlainFailure()
    {
        var (client, _, handler) = Create("Mail.ReadWrite");
        (handler.StatusCode, handler.ResponseBody) = (HttpStatusCode.ServiceUnavailable, "");

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetUnreadMessagesAsync(CancellationToken.None));

        Assert.IsNotType<GraphAccessException>(exception);
    }

    [Fact]
    public async Task CheckingAccess_ReadsTheInboxUnreadCount_AndWhetherReportsCanBeMarkedRead()
    {
        var (client, _, handler) = Create("Mail.ReadWrite");
        handler.ResponseBody = """{"id":"inbox","displayName":"Inbox","unreadItemCount":4,"totalItemCount":10}""";

        var access = await client.CheckAccessAsync(CancellationToken.None);

        Assert.Equal(new MailboxAccess("dmarc-reports@example.com", 4, true), access);
        Assert.EndsWith("users/dmarc-reports@example.com/mailFolders/inbox?$select=unreadItemCount", Uri.UnescapeDataString(handler.Requests[0].RequestUri!.ToString()));
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
    }

    [Fact]
    public async Task CheckingAccess_WithReadOnlyPermission_SaysReportsCantBeMarkedRead()
    {
        var (_, client, handler) = Create("Mail.Read");
        handler.ResponseBody = """{"unreadItemCount":0}""";

        Assert.Equal(new MailboxAccess("tlsrpt@example.com", 0, false), await client.CheckAccessAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CheckingAccess_WithNoMailRolesInTheToken_DoesNotClaimReportsCantBeMarkedRead()
    {
        // An app scoped through Exchange RBAC for Applications has no roles in its token, so what it may do can't be told.
        var (client, _, handler) = Create();
        handler.ResponseBody = """{"unreadItemCount":4}""";

        Assert.Equal(new MailboxAccess("dmarc-reports@example.com", 4, null), await client.CheckAccessAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CheckingAccess_WithoutConsent_ExplainsIt()
    {
        var (client, _, handler) = Create();
        (handler.StatusCode, handler.ResponseBody) = (HttpStatusCode.Forbidden, AccessDenied);

        var exception = await Assert.ThrowsAsync<GraphAccessException>(() => client.CheckAccessAsync(CancellationToken.None));

        Assert.Contains("admin consent", exception.Message);
    }
}
