using System.Buffers;
using System.Net;
using System.Net.Sockets;
using DotMarc.Email;
using MimeKit;
using SmtpServer;
using SmtpServer.Protocol;
using SmtpServer.Storage;
using Xunit;

namespace DotMarc.Tests.Email;

/// <summary>Sends through a real SMTP conversation with an in-process server and reads back what arrived.</summary>
public sealed class SmtpEmailSenderTests
{
    private sealed class CapturingStore : MessageStore
    {
        public List<MimeMessage> Received { get; } = [];

        public override Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
        {
            using var stream = new MemoryStream(buffer.ToArray());
            Received.Add(MimeMessage.Load(stream));
            return Task.FromResult(SmtpResponse.Ok);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task AMessage_ArrivesWithItsRecipientsSubjectSenderAndAttachment()
    {
        var port = FreePort();
        var store = new CapturingStore();
        var options = new SmtpServerOptionsBuilder().ServerName("localhost").Port(port).Build();
        var services = new SmtpServer.ComponentModel.ServiceProvider();
        services.Add(store);
        var server = new SmtpServer.SmtpServer(options, services);
        using var stop = new CancellationTokenSource();
        var running = server.StartAsync(stop.Token);

        var sender = new SmtpEmailSender(new SmtpConnection("localhost", port, SmtpSecurity.None, null, null, "reports@nova-msp.example", "Nova MSP"));
        await sender.SendAsync(new EmailMessage(
            ["it@aurora-retail.example", "finance@aurora-retail.example"], "Aurora Retail Ltd email security report: March 2026",
            "<p>Hello</p>", "Hello", [new EmailAttachment("report.pdf", "application/pdf", [0x25, 0x50, 0x44, 0x46])]), CancellationToken.None);

        stop.Cancel();
        await Task.WhenAny(running, Task.Delay(2000));
        var received = Assert.Single(store.Received);
        Assert.Equal("Aurora Retail Ltd email security report: March 2026", received.Subject);
        Assert.Equal(["it@aurora-retail.example", "finance@aurora-retail.example"], received.To.Mailboxes.Select(mailbox => mailbox.Address));
        Assert.Equal(("Nova MSP", "reports@nova-msp.example"), (received.From.Mailboxes.Single().Name, received.From.Mailboxes.Single().Address));
        Assert.Equal("report.pdf", Assert.Single(received.Attachments).ContentDisposition.FileName);
    }

    [Fact]
    public async Task NoServerListening_IsAnEmailSendException()
    {
        var sender = new SmtpEmailSender(new SmtpConnection("localhost", FreePort(), SmtpSecurity.None, null, null, "reports@nova-msp.example", "Nova MSP"));

        await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(
            new EmailMessage(["it@aurora-retail.example"], "Subject", "<p>Hi</p>", "Hi", []), CancellationToken.None));
    }
}
