using System.Net;
using DotMarc.Graph;
using DotMarc.Tests.Internal;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotMarc.Tests.Graph;

public sealed class MailboxAccessCheckTests
{
    private static IOptions<GraphOptions> Options(string? tlsrptMailbox = null) => Microsoft.Extensions.Options.Options.Create(new GraphOptions
    {
        ClientId = "client-id", TenantId = "tenant-id", ClientSecret = "secret", MailboxAddress = "dmarc-reports@example.com", TlsrptMailboxAddress = tlsrptMailbox,
    });

    [Fact]
    public async Task AWorkingMailbox_SaysHowManyReportsAreWaiting()
    {
        var dmarc = new FakeGraphMailboxClient { Access = new MailboxAccess("dmarc-reports@example.com", 3, true) };

        var results = await new MailboxAccessCheck(dmarc, () => null, Options()).CheckAsync(CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(("DMARC reports", "dmarc-reports@example.com", true, 3, null), (result.Purpose, result.MailboxAddress, result.CanRead, result.UnreadCount, result.Problem));
        Assert.Contains("3 unread", result.Summary);
    }

    [Fact]
    public async Task ReadOnlyAccess_IsAProblem_BecauseReportsStayUnread()
    {
        var dmarc = new FakeGraphMailboxClient { Access = new MailboxAccess("dmarc-reports@example.com", 0, false) };

        var result = Assert.Single(await new MailboxAccessCheck(dmarc, () => null, Options()).CheckAsync(CancellationToken.None));

        Assert.True(result.CanRead);
        Assert.Contains("Mail.ReadWrite", result.Problem);
    }

    [Fact]
    public async Task ARefusal_CarriesGraphsExplanation()
    {
        var dmarc = new FakeGraphMailboxClient { AccessFailure = new GraphAccessException("dotMARC's mail app registration has no Microsoft Graph mail permission.", HttpStatusCode.Forbidden) };

        var result = Assert.Single(await new MailboxAccessCheck(dmarc, () => null, Options()).CheckAsync(CancellationToken.None));

        Assert.False(result.CanRead);
        Assert.Equal("dotMARC's mail app registration has no Microsoft Graph mail permission.", result.Problem);
    }

    [Fact]
    public async Task ASignInFailure_IsReported_NotThrown()
    {
        var dmarc = new FakeGraphMailboxClient { AccessFailure = new InvalidOperationException("AADSTS7000215: Invalid client secret provided.") };

        var result = Assert.Single(await new MailboxAccessCheck(dmarc, () => null, Options()).CheckAsync(CancellationToken.None));

        Assert.False(result.CanRead);
        Assert.Contains("AADSTS7000215", result.Problem);
    }

    [Fact]
    public async Task ATlsrptMailbox_IsCheckedToo_WhenOneIsSetUp()
    {
        var dmarc = new FakeGraphMailboxClient();
        var tlsrpt = new FakeTlsrptMailboxClient { Access = new MailboxAccess("tlsrpt@example.com", 1, true) };

        var results = await new MailboxAccessCheck(dmarc, () => tlsrpt, Options("tlsrpt@example.com")).CheckAsync(CancellationToken.None);

        Assert.Equal(["DMARC reports", "TLS reports"], results.Select(result => result.Purpose));
        Assert.Equal("tlsrpt@example.com", results[1].MailboxAddress);
    }

    [Fact]
    public async Task WithoutATlsrptMailbox_OnlyTheDmarcMailboxIsChecked()
    {
        var results = await new MailboxAccessCheck(new FakeGraphMailboxClient(), () => throw new InvalidOperationException("not configured"), Options()).CheckAsync(CancellationToken.None);

        Assert.Single(results);
    }

    private sealed class FakeTlsrptMailboxClient : ITlsrptGraphMailboxClient
    {
        public MailboxAccess Access { get; set; } = new("tlsrpt@example.com", 0, true);
        public Task<MailboxAccess> CheckAccessAsync(CancellationToken cancellationToken) => Task.FromResult(Access);
        public Task<IReadOnlyList<MailboxMessage>> GetUnreadMessagesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailboxAttachment>> GetAttachmentsAsync(string messageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkAsReadAsync(string messageId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
