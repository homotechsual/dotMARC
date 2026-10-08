# Scheduled Client Reports Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Branded PDF reports per Group, emailed weekly, monthly or quarterly (or on demand) through Microsoft Graph or SMTP.

**Architecture:** A reusable email layer (`IEmailSender` with Graph and SMTP implementations, chosen by an `EmailSettings` singleton) feeds a reporting pipeline: `ReportPeriods` (pure period maths in a configured time zone) and `ClientReportCalculator` (pure aggregation over loaded rows) build a `ClientReport` model, `ClientReportDocument` lays it out with MigraDoc, and `ClientReportDispatcher` (driven by a hosted service) sends due periods with retries, recording `ClientReportDelivery` rows and raising a "Client report failed" alert. Staff manage schedules, send now and download from a Reports dialog on Manage groups.

**Tech Stack:** .NET 10, Blazor Server, MudBlazor 9.8, EF Core 10 with Npgsql, PDFsharp-MigraDoc 6.2 (MIT), MailKit (MIT), xUnit 2.9, Testcontainers Postgres, SmtpServer (MIT, tests only).

**Spec:** `docs/superpowers/specs/2026-10-08-client-reports-design.md`

## Global Constraints

- No em dashes in any user-facing text, docs or comments written for this plan.
- Every user-facing string is plain language; dates in reports use `d MMMM yyyy` with the invariant culture.
- Libraries must be MIT, Apache 2.0 or similar permissive licences; no QuestPDF, no headless browser.
- Email: at most 25 recipients per send; attachments at most 3 MB in total (`EmailLimits`).
- Report time zone defaults to `UTC`, send hour defaults to `6`; an unknown zone id falls back to UTC with a logged warning.
- Weeks run Monday to Sunday; months are calendar months; quarters start in January, April, July and October; boundaries are local midnight in the report time zone.
- Scheduled sends retry hourly; after the first attempt is 24 hours old, the period is marked Failed and the alert raised.
- Static services that change data take `(DotMarcDbContext, AuditActor, ...)` first and are listed in `AuditCoverageTests` (existing rule).
- Tests that need Postgres use `[Collection("Postgres")]` and `fixture.CreateDatabaseAsync()`; `FakeDbContextFactory` throws on unsplit multi-collection includes, so use `AsSplitQuery` when including more than one collection.
- Plan clarification of the spec (ruled here, carried by every task): Graph sends as `EmailSettings.FromAddress`, which must be a mailbox in the tenant; there is no separate `GraphSenderMailbox` column, and the settings page prefills the from address with the configured reports mailbox when Graph is chosen. `FromName` applies to SMTP only (Graph shows the mailbox's own display name).
- Plan clarification of the spec: "change from the previous period" compares with the previous period of the same kind (previous week, month or quarter); a custom range compares with the equal-length range immediately before it.
- Plan clarification of the spec: "Send test email" uses the saved settings, and the page says so.

## Review Focus

1. **Turning a schedule on part-way through a period.** Saving a monthly schedule on 15 March must not immediately send February's report; the first scheduled report is the first period that becomes due after the schedule was turned on. Pinned in Task 5 (`ASchedule_TurnedOnMidPeriod_WaitsForTheNextPeriod`).
2. **Changing the time zone or send hour after reports have gone out.** A period already sent must not be sent again because the zone changed. Pinned in Task 5 (`ChangingTheTimeZone_DoesntResendAPeriod`).
3. **Recipients edited while a scheduled send is failing.** Retries go to the schedule's current recipients, not the list at the first attempt. Pinned in Task 5 (`ARetry_GoesToTheCurrentRecipients`).
4. **A Group renamed while its failure alert is open.** The next successful send still resolves it. Pinned in Task 5 (`ASuccessAfterARename_StillResolvesTheAlert`).
5. **Reports straddling a local midnight.** A report beginning at 23:30 UTC on the last day of the month belongs to the next local day in Europe/Berlin and must be counted in that day's period, not the one before. Pinned in Task 3 (`AReportStraddlingLocalMidnight_CountsInTheLocalDaysPeriod`).

---

### Task 1: Email settings and senders

**Files:**
- Create: `src/DotMarc/Email/EmailSettings.cs`, `src/DotMarc/Email/EmailSettingsService.cs`, `src/DotMarc/Email/EmailMessage.cs`, `src/DotMarc/Email/IEmailSender.cs`, `src/DotMarc/Email/EmailLimits.cs`, `src/DotMarc/Email/SmtpEmailSender.cs`, `src/DotMarc/Email/GraphEmailSender.cs`, `src/DotMarc/Email/EmailSenderFactory.cs`
- Modify: `src/DotMarc/DotMarc.csproj` (MailKit), `src/DotMarc/Data/DotMarcDbContext.cs`, `src/DotMarc/Audit/AuditActions.cs`, `src/DotMarc/Program.cs`, `test/DotMarc.Tests/DotMarc.Tests.csproj` (SmtpServer), `test/DotMarc.Tests/Audit/AuditCoverageTests.cs`
- Create: migration `AddEmailSettings`
- Test: `test/DotMarc.Tests/Email/EmailSettingsServiceTests.cs`, `test/DotMarc.Tests/Email/SmtpEmailSenderTests.cs`, `test/DotMarc.Tests/Email/GraphEmailSenderTests.cs`, `test/DotMarc.Tests/Email/EmailLimitsTests.cs`

**Interfaces:**
- Produces:
  - `enum EmailProvider { Off, Graph, Smtp }`, `enum SmtpSecurity { StartTls, SslOnConnect, None }`
  - `EmailSettings { int Id; EmailProvider Provider; string? FromAddress; string? FromName; string? SmtpHost; int SmtpPort = 587; SmtpSecurity SmtpSecurity; string? SmtpUsername; bool SmtpPasswordConfigured; const string SmtpPasswordSecretKey = "Email.SmtpPassword" }`
  - `EmailSettingsService.GetAsync(DotMarcDbContext, CancellationToken) : Task<EmailSettings>`, `EmailSettingsService.SaveAsync(DotMarcDbContext, AuditActor, ISecretStore, EmailSettings updated, string? newSmtpPassword, CancellationToken)` (throws `ArgumentException` with a field message)
  - `record EmailMessage(IReadOnlyList<string> To, string Subject, string HtmlBody, string TextBody, IReadOnlyList<EmailAttachment> Attachments)`, `record EmailAttachment(string FileName, string ContentType, byte[] Bytes)`
  - `interface IEmailSender { Task SendAsync(EmailMessage message, CancellationToken cancellationToken); }`, `sealed class EmailSendException(string message, Exception? inner = null) : Exception`
  - `EmailLimits.MaximumRecipients = 25`, `EmailLimits.MaximumAttachmentBytes = 3 * 1024 * 1024`, `EmailLimits.Check(EmailMessage)` (throws `EmailSendException`)
  - `interface IEmailSenderFactory { Task<IEmailSender?> GetAsync(CancellationToken cancellationToken); }` (null when email is Off or in the demo)
  - `AuditActions.EmailSettingsSaved = "settings.email.saved"` ("Email settings saved")

- [ ] **Step 1: Add packages**

```bash
dotnet add src/DotMarc/DotMarc.csproj package MailKit
dotnet add test/DotMarc.Tests/DotMarc.Tests.csproj package SmtpServer
dotnet add test/DotMarc.Tests/DotMarc.Tests.csproj package MimeKit
```

Expected: restore succeeds. (MimeKit comes with MailKit in the app; the test project uses it directly to read received messages.)

- [ ] **Step 2: Write the failing tests**

```csharp
// test/DotMarc.Tests/Email/EmailLimitsTests.cs
using DotMarc.Email;
using Xunit;

namespace DotMarc.Tests.Email;

public sealed class EmailLimitsTests
{
    private static EmailMessage Message(int recipients, int attachmentBytes) => new(
        Enumerable.Range(1, recipients).Select(number => $"person{number}@example.com").ToList(),
        "Subject", "<p>Hi</p>", "Hi",
        attachmentBytes == 0 ? [] : [new EmailAttachment("report.pdf", "application/pdf", new byte[attachmentBytes])]);

    [Fact]
    public void AMessageWithinTheLimits_Passes() => EmailLimits.Check(Message(25, EmailLimits.MaximumAttachmentBytes));

    [Theory]
    [InlineData(0, 0, "There's no one to send this to.")]
    [InlineData(26, 0, "A report can go to at most 25 recipients.")]
    [InlineData(1, EmailLimits.MaximumAttachmentBytes + 1, "The attachment is over 3 MB, too large to send.")]
    public void AMessageOutsideTheLimits_IsRefused(int recipients, int attachmentBytes, string expected)
    {
        var exception = Assert.Throws<EmailSendException>(() => EmailLimits.Check(Message(recipients, attachmentBytes)));

        Assert.Equal(expected, exception.Message);
    }
}
```

```csharp
// test/DotMarc.Tests/Email/EmailSettingsServiceTests.cs
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Email;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Email;

[Collection("Postgres")]
public sealed class EmailSettingsServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public EmailSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    [Fact]
    public async Task EmailStartsOff()
    {
        await using var context = CreateContext();

        Assert.Equal(EmailProvider.Off, (await EmailSettingsService.GetAsync(context)).Provider);
    }

    [Fact]
    public async Task SavingSmtp_StoresThePasswordAsASecret_AndAuditsWithoutIt()
    {
        await using var context = CreateContext();
        var secrets = new FakeSecretStore();
        var updated = await EmailSettingsService.GetAsync(context);
        updated.Provider = EmailProvider.Smtp;
        updated.FromAddress = "reports@nova-msp.example";
        updated.SmtpHost = "smtp.nova-msp.example";

        await EmailSettingsService.SaveAsync(context, TestActors.Admin, secrets, updated, "the-password");

        await using var verify = CreateContext();
        var saved = await EmailSettingsService.GetAsync(verify);
        Assert.Equal((EmailProvider.Smtp, true), (saved.Provider, saved.SmtpPasswordConfigured));
        Assert.Equal("the-password", secrets.Secrets[EmailSettings.SmtpPasswordSecretKey]);
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.EmailSettingsSaved, entry.Action);
        Assert.DoesNotContain(entry.Changes, change => change.New == "the-password");
    }

    [Theory]
    [InlineData(EmailProvider.Graph, null, null, "From address is required to send email.")]
    [InlineData(EmailProvider.Graph, "not-an-email", null, "From address isn't a valid email address.")]
    [InlineData(EmailProvider.Smtp, "reports@nova-msp.example", null, "SMTP host is required.")]
    public async Task Save_RefusesIncompleteSettings(EmailProvider provider, string? fromAddress, string? smtpHost, string message)
    {
        await using var context = CreateContext();
        var updated = await EmailSettingsService.GetAsync(context);
        updated.Provider = provider;
        updated.FromAddress = fromAddress;
        updated.SmtpHost = smtpHost;

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            EmailSettingsService.SaveAsync(context, TestActors.Admin, new FakeSecretStore(), updated, null));

        Assert.StartsWith(message, exception.Message);
    }

    [Fact]
    public async Task Save_RefusesAPortOutOfRange()
    {
        await using var context = CreateContext();
        var updated = await EmailSettingsService.GetAsync(context);
        updated.Provider = EmailProvider.Smtp;
        updated.FromAddress = "reports@nova-msp.example";
        updated.SmtpHost = "smtp.nova-msp.example";
        updated.SmtpPort = 70000;

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            EmailSettingsService.SaveAsync(context, TestActors.Admin, new FakeSecretStore(), updated, null));

        Assert.StartsWith("SMTP port must be between 1 and 65535.", exception.Message);
    }
}
```

```csharp
// test/DotMarc.Tests/Email/SmtpEmailSenderTests.cs
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
```

(The `SmtpServer` package's API is as of its current major version: `SmtpServerOptionsBuilder`, `SmtpServer.ComponentModel.ServiceProvider` and `MessageStore.SaveAsync`. If the installed version differs, adapt the fixture, not the assertions.)

```csharp
// test/DotMarc.Tests/Email/GraphEmailSenderTests.cs
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
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DotMarc.Tests.Email"`
Expected: build FAIL (types missing).

- [ ] **Step 4: Implement**

```csharp
// src/DotMarc/Email/EmailSettings.cs
namespace DotMarc.Email;

public enum EmailProvider { Off, Graph, Smtp }

public enum SmtpSecurity { StartTls, SslOnConnect, None }

/// <summary>Singleton row (seeded <c>Id = 1</c>) saying how dotMARC sends email. Graph sends as
/// <see cref="FromAddress"/>, which must be a mailbox in the tenant; SMTP uses the server below. The SMTP password
/// lives in ISecretStore under <see cref="SmtpPasswordSecretKey"/>, never on this entity.</summary>
public sealed class EmailSettings
{
    public const string SmtpPasswordSecretKey = "Email.SmtpPassword";

    public int Id { get; set; }
    public EmailProvider Provider { get; set; }
    public string? FromAddress { get; set; }

    /// <summary>The sender's display name for SMTP. When empty, the MSP brand's product name is used.</summary>
    public string? FromName { get; set; }

    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public SmtpSecurity SmtpSecurity { get; set; }
    public string? SmtpUsername { get; set; }
    public bool SmtpPasswordConfigured { get; set; }
}
```

```csharp
// src/DotMarc/Email/EmailMessage.cs
namespace DotMarc.Email;

public sealed record EmailMessage(IReadOnlyList<string> To, string Subject, string HtmlBody, string TextBody, IReadOnlyList<EmailAttachment> Attachments);

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Bytes);

/// <summary>A send that failed, carrying a message fit to show staff (the provider's own words where it gave any).</summary>
public sealed class EmailSendException(string message, Exception? inner = null) : Exception(message, inner);
```

```csharp
// src/DotMarc/Email/IEmailSender.cs
namespace DotMarc.Email;

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>The sender for the saved email settings, or null when email is Off (always, in the demo).</summary>
public interface IEmailSenderFactory
{
    Task<IEmailSender?> GetAsync(CancellationToken cancellationToken);
}
```

```csharp
// src/DotMarc/Email/EmailLimits.cs
namespace DotMarc.Email;

/// <summary>Checked before any provider is contacted, so an oversized send fails clearly rather than half-way.</summary>
public static class EmailLimits
{
    public const int MaximumRecipients = 25;

    /// <summary>Graph's limit for an attachment sent inline with the message. A report is typically well under 1 MB.</summary>
    public const int MaximumAttachmentBytes = 3 * 1024 * 1024;

    public static void Check(EmailMessage message)
    {
        if (message.To.Count == 0)
        {
            throw new EmailSendException("There's no one to send this to.");
        }

        if (message.To.Count > MaximumRecipients)
        {
            throw new EmailSendException($"A report can go to at most {MaximumRecipients} recipients.");
        }

        if (message.Attachments.Sum(attachment => (long)attachment.Bytes.Length) > MaximumAttachmentBytes)
        {
            throw new EmailSendException("The attachment is over 3 MB, too large to send.");
        }
    }
}
```

```csharp
// src/DotMarc/Email/SmtpEmailSender.cs
using MailKit.Security;
using MimeKit;

namespace DotMarc.Email;

public sealed record SmtpConnection(string Host, int Port, SmtpSecurity Security, string? Username, string? Password, string FromAddress, string FromName);

/// <summary>Sends through an SMTP server with MailKit. All recipients go in To, in one message.</summary>
public sealed class SmtpEmailSender(SmtpConnection connection) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        EmailLimits.Check(message);

        var mime = new MimeMessage { Subject = message.Subject };
        mime.From.Add(new MailboxAddress(connection.FromName, connection.FromAddress));
        mime.To.AddRange(message.To.Select(address => MailboxAddress.Parse(address)));
        var body = new BodyBuilder { HtmlBody = message.HtmlBody, TextBody = message.TextBody };
        foreach (var attachment in message.Attachments)
        {
            body.Attachments.Add(attachment.FileName, attachment.Bytes, ContentType.Parse(attachment.ContentType));
        }

        mime.Body = body.ToMessageBody();

        using var client = new MailKit.Net.Smtp.SmtpClient();
        try
        {
            var socketOptions = connection.Security switch
            {
                SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
                SmtpSecurity.None => SecureSocketOptions.None,
                _ => SecureSocketOptions.StartTls,
            };
            await client.ConnectAsync(connection.Host, connection.Port, socketOptions, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(connection.Username))
            {
                await client.AuthenticateAsync(connection.Username, connection.Password ?? "", cancellationToken).ConfigureAwait(false);
            }

            await client.SendAsync(mime, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not EmailSendException)
        {
            throw new EmailSendException($"The SMTP server refused the message: {exception.Message}", exception);
        }
    }
}
```

```csharp
// src/DotMarc/Email/GraphEmailSender.cs
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

        using var request = new HttpRequestMessage(HttpMethod.Post, $"users/{Uri.EscapeDataString(mailbox).Replace("%40", "@")}/sendMail")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
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
```

```csharp
// src/DotMarc/Email/EmailSettingsService.cs
using System.Net.Mail;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Email;

/// <summary>Reads and saves how dotMARC sends email.</summary>
public static class EmailSettingsService
{
    public static Task<EmailSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.EmailSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, ISecretStore secretStore, EmailSettings updated, string? newSmtpPassword, CancellationToken cancellationToken = default)
    {
        Normalise(updated);
        Validate(updated);

        // A no-tracking snapshot, because the caller may pass the very instance this context is tracking.
        var saved = await context.EmailSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var hasNewPassword = !string.IsNullOrEmpty(newSmtpPassword);
        var changes = new AuditChanges()
            .Field("Provider", saved.Provider, updated.Provider)
            .Field("From address", saved.FromAddress, updated.FromAddress)
            .Field("From name", saved.FromName, updated.FromName)
            .Field("SMTP host", saved.SmtpHost, updated.SmtpHost)
            .Field("SMTP port", saved.SmtpPort, updated.SmtpPort)
            .Field("SMTP security", saved.SmtpSecurity, updated.SmtpSecurity)
            .Field("SMTP username", saved.SmtpUsername, updated.SmtpUsername)
            .Secret("SMTP password", hasNewPassword);
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.EmailSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.Provider = updated.Provider;
        existing.FromAddress = updated.FromAddress;
        existing.FromName = updated.FromName;
        existing.SmtpHost = updated.SmtpHost;
        existing.SmtpPort = updated.SmtpPort;
        existing.SmtpSecurity = updated.SmtpSecurity;
        existing.SmtpUsername = updated.SmtpUsername;
        if (hasNewPassword)
        {
            await secretStore.SetSecretAsync(EmailSettings.SmtpPasswordSecretKey, newSmtpPassword!, cancellationToken).ConfigureAwait(false);
            existing.SmtpPasswordConfigured = true;
        }

        AuditLog.Record(context, actor, AuditActions.EmailSettingsSaved, AuditTarget.Settings("Email"), "Saved email settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Normalise(EmailSettings settings)
    {
        settings.FromAddress = Blank(settings.FromAddress);
        settings.FromName = Blank(settings.FromName);
        settings.SmtpHost = Blank(settings.SmtpHost);
        settings.SmtpUsername = Blank(settings.SmtpUsername);
    }

    private static void Validate(EmailSettings settings)
    {
        if (settings.Provider == EmailProvider.Off)
        {
            return;
        }

        if (settings.FromAddress is null) throw new ArgumentException("From address is required to send email.", nameof(settings));
        if (!MailAddress.TryCreate(settings.FromAddress, out _) || settings.FromAddress.Length > 254) throw new ArgumentException("From address isn't a valid email address.", nameof(settings));
        if (settings.FromName is { Length: > 100 }) throw new ArgumentException("From name can be at most 100 characters.", nameof(settings));
        if (settings.Provider != EmailProvider.Smtp)
        {
            return;
        }

        if (settings.SmtpHost is null) throw new ArgumentException("SMTP host is required.", nameof(settings));
        if (settings.SmtpHost.Length > 253) throw new ArgumentException("SMTP host can be at most 253 characters.", nameof(settings));
        if (settings.SmtpPort is < 1 or > 65535) throw new ArgumentException("SMTP port must be between 1 and 65535.", nameof(settings));
        if (settings.SmtpUsername is { Length: > 254 }) throw new ArgumentException("SMTP username can be at most 254 characters.", nameof(settings));
    }
}
```

```csharp
// src/DotMarc/Email/EmailSenderFactory.cs
using DotMarc.Data;
using DotMarc.Demo;
using DotMarc.Graph;
using DotMarc.Notifications;
using DotMarc.Portal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DotMarc.Email;

/// <summary>Builds the sender for the saved settings each time it's asked, so a settings change takes effect at once.
/// The demo never sends email, whatever its settings say.</summary>
public sealed class EmailSenderFactory(
    IDbContextFactory<DotMarcDbContext> dbFactory,
    ISecretStore secretStore,
    IHttpClientFactory httpClientFactory,
    IServiceProvider services,
    IOptions<DemoOptions> demoOptions) : IEmailSenderFactory
{
    public async Task<IEmailSender?> GetAsync(CancellationToken cancellationToken)
    {
        if (demoOptions.Value.Enabled)
        {
            return null;
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await EmailSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        switch (settings.Provider)
        {
            case EmailProvider.Graph:
                return new GraphEmailSender(httpClientFactory.CreateClient(GraphEmailSender.HttpClientName),
                    services.GetRequiredService<IGraphTokenProvider>(), settings.FromAddress!);
            case EmailProvider.Smtp:
                var password = settings.SmtpPasswordConfigured
                    ? await secretStore.GetSecretAsync(EmailSettings.SmtpPasswordSecretKey, cancellationToken).ConfigureAwait(false)
                    : null;
                var fromName = settings.FromName ?? (await BrandingSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false)).ProductName;
                return new SmtpEmailSender(new SmtpConnection(settings.SmtpHost!, settings.SmtpPort, settings.SmtpSecurity,
                    settings.SmtpUsername, password, settings.FromAddress!, fromName));
            default:
                return null;
        }
    }
}
```

(Check the demo options type and namespace with `grep -rn "class DemoOptions" src/DotMarc`; use whatever it is.)

`DotMarcDbContext`: `public DbSet<DotMarc.Email.EmailSettings> EmailSettings => Set<DotMarc.Email.EmailSettings>();` and, beside the other singletons:

```csharp
modelBuilder.Entity<DotMarc.Email.EmailSettings>(entity =>
{
    entity.Property(settings => settings.Provider).HasConversion<string>().HasMaxLength(10);
    entity.Property(settings => settings.SmtpSecurity).HasConversion<string>().HasMaxLength(15);
    entity.Property(settings => settings.FromAddress).HasMaxLength(254);
    entity.Property(settings => settings.FromName).HasMaxLength(100);
    entity.Property(settings => settings.SmtpHost).HasMaxLength(253);
    entity.Property(settings => settings.SmtpUsername).HasMaxLength(254);
    entity.HasData(new DotMarc.Email.EmailSettings { Id = 1 });
});
```

`AuditActions`: the constant beside `BrandingSettingsSaved` and `(EmailSettingsSaved, "Email settings saved")` in the labels list. `AuditCoverageTests`: add `typeof(DotMarc.Email.EmailSettingsService)`.

`Program.cs`, near the other HTTP clients:

```csharp
builder.Services.AddHttpClient(DotMarc.Email.GraphEmailSender.HttpClientName, client => client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"));
builder.Services.AddSingleton<DotMarc.Email.IEmailSenderFactory, DotMarc.Email.EmailSenderFactory>();
```

(Check the base address the existing Graph mailbox clients use and match it exactly.)

Run: `dotnet ef migrations add AddEmailSettings --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj`

- [ ] **Step 5: Run the tests**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DotMarc.Tests.Email|FullyQualifiedName~AuditCoverageTests"`
Expected: PASS. Then `dotnet test test/DotMarc.Tests` (all pass, including `ProgramDiValidationTests`).

- [ ] **Step 6: Commit**

```bash
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Send email through Microsoft Graph or SMTP"
```

---

### Task 2: Periods, report settings and schedules

**Files:**
- Create: `src/DotMarc/Reporting/ClientReports/ReportPeriod.cs`, `src/DotMarc/Reporting/ClientReports/ReportSettings.cs`, `src/DotMarc/Reporting/ClientReports/ReportSettingsService.cs`, `src/DotMarc/Reporting/ClientReports/GroupReportSchedule.cs`, `src/DotMarc/Reporting/ClientReports/ClientReportDelivery.cs`, `src/DotMarc/Reporting/ClientReports/ClientReportService.cs`, `src/DotMarc/Reporting/ClientReports/ClientReportAccess.cs`
- Modify: `src/DotMarc/Data/Permission.cs`, `src/DotMarc/Data/DotMarcDbContext.cs`, `src/DotMarc/Audit/AuditActions.cs`, `src/DotMarc/Demo/DemoDataSeeder.cs` (truncate list), `test/DotMarc.Tests/Audit/AuditCoverageTests.cs`
- Create: migration `AddClientReports`
- Test: `test/DotMarc.Tests/Reporting/ClientReports/ReportPeriodTests.cs`, `test/DotMarc.Tests/Reporting/ClientReports/ClientReportServiceTests.cs`, `test/DotMarc.Tests/Reporting/ClientReports/ReportSettingsServiceTests.cs`

**Interfaces:**
- Produces:
  - `enum ReportFrequency { Off, Weekly, Monthly, Quarterly }`, `enum ReportPeriodKind { Week, Month, Quarter, Custom }`
  - `record ReportPeriod(DateOnly Start, DateOnly End, ReportPeriodKind Kind)` with `string Label`, `int Days`
  - `ReportPeriods.Previous(ReportFrequency, TimeZoneInfo, DateTimeOffset nowUtc) : ReportPeriod`, `ReportPeriods.Recent(ReportPeriodKind, TimeZoneInfo, DateTimeOffset nowUtc, int count) : IReadOnlyList<ReportPeriod>`, `ReportPeriods.FromRange(DateOnly start, DateOnly end, TimeZoneInfo, DateTimeOffset nowUtc) : ReportPeriod` (throws `ArgumentException`), `ReportPeriods.PreviousForComparison(ReportPeriod) : ReportPeriod`, `ReportPeriods.StartUtc(ReportPeriod, TimeZoneInfo)`, `ReportPeriods.EndUtc(ReportPeriod, TimeZoneInfo)` (exclusive), `ReportPeriods.DueUtc(ReportPeriod, TimeZoneInfo, int sendHour)`, `ReportPeriods.LocalDay(DateTimeOffset utc, TimeZoneInfo) : DateOnly`
  - `ReportSettings { int Id; string TimeZoneId = "UTC"; int SendHour = 6 }`, `ReportSettingsService.GetAsync`, `ReportSettingsService.SaveAsync(DotMarcDbContext, AuditActor, ReportSettings updated, CancellationToken)`, `ReportSettingsService.ResolveZone(string timeZoneId, ILogger? logger = null) : TimeZoneInfo`, `ReportSettingsService.AvailableZones() : IReadOnlyList<string>`
  - `GroupReportSchedule { int GroupId; ReportFrequency Frequency; List<string> Recipients; DateTimeOffset StartedUtc }`
  - `enum ClientReportDeliveryKind { Scheduled, Manual }`, `enum ClientReportDeliveryStatus { Pending, Sent, Failed, Skipped }`, `ClientReportDelivery { int Id; int GroupId; DateOnly PeriodStart; DateOnly PeriodEnd; ClientReportDeliveryKind Kind; List<string> Recipients; string? RequestedBy; ClientReportDeliveryStatus Status; int Attempts; DateTimeOffset? FirstAttemptUtc; DateTimeOffset? LastAttemptUtc; DateTimeOffset? SentUtc; string? Error }`
  - `ClientReportService.GetScheduleAsync(DotMarcDbContext, int groupId, CancellationToken) : Task<GroupReportSchedule?>`, `ClientReportService.SetScheduleAsync(DotMarcDbContext, AuditActor, int groupId, ReportFrequency, IReadOnlyList<string> recipients, CancellationToken)`, `ClientReportService.NormaliseRecipients(IEnumerable<string>) : List<string>` (throws `ArgumentException`), `ClientReportService.SuggestRecipientsAsync(DotMarcDbContext, int groupId, CancellationToken) : Task<IReadOnlyList<string>>`, `ClientReportService.ListDeliveriesAsync(DotMarcDbContext, int groupId, int count, CancellationToken)`
  - `ClientReportAccess.MayManage(ClaimsPrincipal user, int groupId) : bool`
  - `Permission.ReportsManage`
  - `AuditActions.ReportSettingsSaved = "settings.reports.saved"` ("Report settings saved"), `AuditActions.GroupReportScheduleChanged = "group.report_schedule_changed"` ("Group report schedule changed"), `AuditActions.ClientReportSent = "group.report_sent"` ("Client report sent")

- [ ] **Step 1: Write the failing tests**

```csharp
// test/DotMarc.Tests/Reporting/ClientReports/ReportPeriodTests.cs
using DotMarc.Reporting.ClientReports;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

public sealed class ReportPeriodTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void ThePreviousWeek_RunsMondayToSunday()
    {
        var period = ReportPeriods.Previous(ReportFrequency.Weekly, TimeZoneInfo.Utc, Utc(2026, 3, 11)); // a Wednesday

        Assert.Equal((new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 8), "Week of 2 March 2026"), (period.Start, period.End, period.Label));
    }

    [Fact]
    public void ThePreviousMonth_IsTheLastCalendarMonth_AcrossAYearEnd()
    {
        var period = ReportPeriods.Previous(ReportFrequency.Monthly, TimeZoneInfo.Utc, Utc(2026, 1, 15));

        Assert.Equal((new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31), "December 2025"), (period.Start, period.End, period.Label));
    }

    [Fact]
    public void ThePreviousQuarter_IsTheLastCalendarQuarter()
    {
        var period = ReportPeriods.Previous(ReportFrequency.Quarterly, TimeZoneInfo.Utc, Utc(2026, 5, 20));

        Assert.Equal((new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), "Q1 2026"), (period.Start, period.End, period.Label));
    }

    [Fact]
    public void ThePeriod_UsesTheLocalDate_NotTheUtcDate()
    {
        // 31 March 14:00 UTC is already 1 April in Sydney, so March is the previous month there.
        var period = ReportPeriods.Previous(ReportFrequency.Monthly, Sydney, Utc(2026, 3, 31, 14));

        Assert.Equal(new DateOnly(2026, 3, 1), period.Start);
    }

    [Fact]
    public void APeriod_StartsAndEndsAtLocalMidnight()
    {
        var march = new ReportPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), ReportPeriodKind.Month);

        // London is on GMT on 1 March and on BST (UTC+1) from 29 March.
        Assert.Equal((Utc(2026, 3, 1), Utc(2026, 3, 31, 23)), (ReportPeriods.StartUtc(march, London), ReportPeriods.EndUtc(march, London)));
    }

    [Fact]
    public void AReport_IsDueAtTheSendHourOnTheFirstLocalDayAfterThePeriod()
    {
        var march = new ReportPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), ReportPeriodKind.Month);

        Assert.Equal(Utc(2026, 4, 1, 5), ReportPeriods.DueUtc(march, London, sendHour: 6)); // 06:00 BST
        Assert.Equal(Utc(2026, 3, 31, 19), ReportPeriods.DueUtc(march, Sydney, sendHour: 6)); // 06:00 AEDT
    }

    [Fact]
    public void ASendHourThatDoesntExist_BecomesTheFirstValidTimeAfterIt()
    {
        // London's clocks go from 01:00 to 02:00 on 29 March 2026, so 01:00 that day doesn't exist.
        var week = new ReportPeriod(new DateOnly(2026, 3, 22), new DateOnly(2026, 3, 28), ReportPeriodKind.Week);

        Assert.Equal(Utc(2026, 3, 29, 1), ReportPeriods.DueUtc(week, London, sendHour: 1));
    }

    [Fact]
    public void AMonthsComparison_IsThePreviousMonth_AndACustomRangesIsTheEqualLengthBefore()
    {
        var march = new ReportPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), ReportPeriodKind.Month);
        var custom = new ReportPeriod(new DateOnly(2026, 3, 11), new DateOnly(2026, 3, 20), ReportPeriodKind.Custom);

        Assert.Equal(new ReportPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), ReportPeriodKind.Month), ReportPeriods.PreviousForComparison(march));
        Assert.Equal(new ReportPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 10), ReportPeriodKind.Custom), ReportPeriods.PreviousForComparison(custom));
    }

    [Theory]
    [InlineData("2026-03-02", "2026-03-08", ReportPeriodKind.Week, "Week of 2 March 2026")]
    [InlineData("2026-02-01", "2026-02-28", ReportPeriodKind.Month, "February 2026")]
    [InlineData("2026-01-01", "2026-03-31", ReportPeriodKind.Quarter, "Q1 2026")]
    [InlineData("2026-01-01", "2026-03-15", ReportPeriodKind.Custom, "1 January 2026 to 15 March 2026")]
    public void ARange_IsRecognisedAsTheKindOfPeriodItCovers(string start, string end, ReportPeriodKind kind, string label)
    {
        var period = ReportPeriods.FromRange(DateOnly.Parse(start), DateOnly.Parse(end), TimeZoneInfo.Utc, Utc(2026, 4, 2));

        Assert.Equal((kind, label), (period.Kind, period.Label));
    }

    [Theory]
    [InlineData("2026-03-10", "2026-03-01", "The start date must be on or before the end date.")]
    [InlineData("2026-03-01", "2026-04-02", "A report can only cover days that have ended.")]
    [InlineData("2025-01-01", "2026-03-01", "A report can cover at most 366 days.")]
    public void AnInvalidRange_IsRefused(string start, string end, string message)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            ReportPeriods.FromRange(DateOnly.Parse(start), DateOnly.Parse(end), TimeZoneInfo.Utc, Utc(2026, 4, 2, 12)));

        Assert.StartsWith(message, exception.Message);
    }

    [Fact]
    public void RecentPeriods_AreTheLastCompleteOnes_NewestFirst()
    {
        var months = ReportPeriods.Recent(ReportPeriodKind.Month, TimeZoneInfo.Utc, Utc(2026, 4, 2), count: 3);

        Assert.Equal(["March 2026", "February 2026", "January 2026"], months.Select(period => period.Label));
    }

    [Fact]
    public void AnUnknownTimeZone_FallsBackToUtc() =>
        Assert.Equal(TimeZoneInfo.Utc, ReportSettingsService.ResolveZone("Mars/Olympus_Mons"));
}
```

```csharp
// test/DotMarc.Tests/Reporting/ClientReports/ClientReportServiceTests.cs
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Reporting.ClientReports;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

[Collection("Postgres")]
public sealed class ClientReportServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ClientReportServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    private async Task<Group> AddGroupAsync(DotMarcDbContext context, string name = "Aurora Retail")
    {
        var group = new Group { Name = name };
        context.Groups.Add(group);
        await context.SaveChangesAsync();
        return group;
    }

    [Fact]
    public async Task SetSchedule_StoresTidiedRecipients_AndAudits()
    {
        await using var context = CreateContext();
        var group = await AddGroupAsync(context);

        await ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Monthly, [" IT@Aurora-Retail.example ", "it@aurora-retail.example", "finance@aurora-retail.example"]);

        await using var verify = CreateContext();
        var schedule = await ClientReportService.GetScheduleAsync(verify, group.Id);
        Assert.Equal(ReportFrequency.Monthly, schedule!.Frequency);
        Assert.Equal(["finance@aurora-retail.example", "it@aurora-retail.example"], schedule.Recipients);
        Assert.Equal(AuditActions.GroupReportScheduleChanged, (await verify.AuditEntries.SingleAsync()).Action);
    }

    [Theory]
    [InlineData(new[] { "not-an-email" }, "not-an-email isn't a valid email address.")]
    [InlineData(new string[0], "Add at least one recipient, or turn the schedule off.")]
    public async Task SetSchedule_RefusesBadRecipients(string[] recipients, string message)
    {
        await using var context = CreateContext();
        var group = await AddGroupAsync(context);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Weekly, recipients));

        Assert.StartsWith(message, exception.Message);
    }

    [Fact]
    public async Task SetSchedule_RefusesMoreThan25Recipients()
    {
        await using var context = CreateContext();
        var group = await AddGroupAsync(context);
        var recipients = Enumerable.Range(1, 26).Select(number => $"person{number}@aurora-retail.example").ToList();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Weekly, recipients));

        Assert.StartsWith("A report can go to at most 25 recipients.", exception.Message);
    }

    [Fact]
    public async Task ChangingTheFrequency_RestartsTheSchedule_ButEditingRecipientsDoesnt()
    {
        await using var context = CreateContext();
        var group = await AddGroupAsync(context);
        await ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Monthly, ["it@aurora-retail.example"]);
        var started = (await ClientReportService.GetScheduleAsync(context, group.Id))!.StartedUtc;

        await ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Monthly, ["it@aurora-retail.example", "ceo@aurora-retail.example"]);
        var afterRecipients = (await ClientReportService.GetScheduleAsync(context, group.Id))!.StartedUtc;
        await Task.Delay(20);
        await ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Quarterly, ["it@aurora-retail.example"]);
        var afterFrequency = (await ClientReportService.GetScheduleAsync(context, group.Id))!.StartedUtc;

        Assert.Equal(started, afterRecipients);
        Assert.True(afterFrequency > started);
    }

    [Fact]
    public async Task TurningItOffWithNoRecipients_RemovesTheSchedule()
    {
        await using var context = CreateContext();
        var group = await AddGroupAsync(context);
        await ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Monthly, ["it@aurora-retail.example"]);

        await ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Off, []);

        await using var verify = CreateContext();
        Assert.Null(await ClientReportService.GetScheduleAsync(verify, group.Id));
    }

    [Fact]
    public async Task Suggestions_AreTheEmailsOfGrantsScopedToTheGroup()
    {
        await using var context = CreateContext();
        var aurora = await AddGroupAsync(context);
        var brightline = await AddGroupAsync(context, "Brightline Legal");
        var viewer = await context.Roles.SingleAsync(role => role.Name == "Viewer");
        context.UserAccesses.AddRange(
            new UserAccess { Email = "it@aurora-retail.example", Role = viewer, ScopedGroups = [aurora] },
            new UserAccess { Email = "partner@brightline-legal.example", Role = viewer, ScopedGroups = [brightline] });
        await context.SaveChangesAsync();

        Assert.Equal(["it@aurora-retail.example"], await ClientReportService.SuggestRecipientsAsync(context, aurora.Id));
    }

    [Fact]
    public async Task DeletingAGroup_DeletesItsScheduleAndDeliveries()
    {
        await using var context = CreateContext();
        var group = await AddGroupAsync(context);
        await ClientReportService.SetScheduleAsync(context, TestActors.Admin, group.Id, ReportFrequency.Monthly, ["it@aurora-retail.example"]);
        context.ClientReportDeliveries.Add(new ClientReportDelivery
        {
            GroupId = group.Id, PeriodStart = new DateOnly(2026, 3, 1), PeriodEnd = new DateOnly(2026, 3, 31),
            Kind = ClientReportDeliveryKind.Scheduled, Recipients = ["it@aurora-retail.example"], Status = ClientReportDeliveryStatus.Sent,
        });
        await context.SaveChangesAsync();

        await GroupManagementService.RemoveGroupAsync(context, TestActors.Admin, group.Id);

        await using var verify = CreateContext();
        Assert.Equal((0, 0), (await verify.GroupReportSchedules.CountAsync(), await verify.ClientReportDeliveries.CountAsync()));
    }
}
```

(Check the seeded Viewer role exists after migrations in a fresh test database; if roles are only created by `AccessBootstrapper`, create a `new Role { Name = "Viewer", IsScopable = true, Permissions = [Permission.DomainsView] }` in the test instead.)

```csharp
// test/DotMarc.Tests/Reporting/ClientReports/ReportSettingsServiceTests.cs
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Reporting.ClientReports;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

[Collection("Postgres")]
public sealed class ReportSettingsServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ReportSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    [Fact]
    public async Task TheDefaults_AreUtcAt6()
    {
        await using var context = CreateContext();

        var settings = await ReportSettingsService.GetAsync(context);

        Assert.Equal(("UTC", 6), (settings.TimeZoneId, settings.SendHour));
    }

    [Fact]
    public async Task Save_StoresTheZoneAndHour_AndAudits()
    {
        await using var context = CreateContext();
        var updated = await ReportSettingsService.GetAsync(context);
        updated.TimeZoneId = "Europe/London";
        updated.SendHour = 7;

        await ReportSettingsService.SaveAsync(context, TestActors.Admin, updated);

        await using var verify = CreateContext();
        var saved = await ReportSettingsService.GetAsync(verify);
        Assert.Equal(("Europe/London", 7), (saved.TimeZoneId, saved.SendHour));
        Assert.Equal(AuditActions.ReportSettingsSaved, (await verify.AuditEntries.SingleAsync()).Action);
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons", 6, "Mars/Olympus_Mons isn't a time zone this server knows.")]
    [InlineData("UTC", 24, "Send hour must be between 0 and 23.")]
    public async Task Save_RefusesAnUnknownZoneOrHour(string zone, int hour, string message)
    {
        await using var context = CreateContext();
        var updated = await ReportSettingsService.GetAsync(context);
        updated.TimeZoneId = zone;
        updated.SendHour = hour;

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => ReportSettingsService.SaveAsync(context, TestActors.Admin, updated));

        Assert.StartsWith(message, exception.Message);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DotMarc.Tests.Reporting.ClientReports"`
Expected: build FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/DotMarc/Reporting/ClientReports/ReportPeriod.cs
using System.Globalization;

namespace DotMarc.Reporting.ClientReports;

public enum ReportFrequency { Off, Weekly, Monthly, Quarterly }

public enum ReportPeriodKind { Week, Month, Quarter, Custom }

/// <summary>The days a report covers, inclusive, in the report time zone.</summary>
public sealed record ReportPeriod(DateOnly Start, DateOnly End, ReportPeriodKind Kind)
{
    public int Days => End.DayNumber - Start.DayNumber + 1;

    public string Label => Kind switch
    {
        ReportPeriodKind.Week => $"Week of {Format(Start)}",
        ReportPeriodKind.Month => Start.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
        ReportPeriodKind.Quarter => string.Create(CultureInfo.InvariantCulture, $"Q{((Start.Month - 1) / 3) + 1} {Start.Year}"),
        _ => $"{Format(Start)} to {Format(End)}",
    };

    public static string Format(DateOnly day) => day.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
}

/// <summary>Period maths in the report time zone. Weeks run Monday to Sunday; quarters start in January, April, July
/// and October; every boundary is local midnight.</summary>
public static class ReportPeriods
{
    public const int MaximumDays = 366;

    public static DateOnly LocalDay(DateTimeOffset utc, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);

    public static ReportPeriod Week(DateOnly monday) => new(monday, monday.AddDays(6), ReportPeriodKind.Week);

    public static ReportPeriod Month(DateOnly first) => new(first, first.AddMonths(1).AddDays(-1), ReportPeriodKind.Month);

    public static ReportPeriod Quarter(DateOnly first) => new(first, first.AddMonths(3).AddDays(-1), ReportPeriodKind.Quarter);

    /// <summary>The last complete period of the frequency at <paramref name="nowUtc"/>.</summary>
    public static ReportPeriod Previous(ReportFrequency frequency, TimeZoneInfo zone, DateTimeOffset nowUtc) => frequency switch
    {
        ReportFrequency.Weekly => Recent(ReportPeriodKind.Week, zone, nowUtc, 1)[0],
        ReportFrequency.Monthly => Recent(ReportPeriodKind.Month, zone, nowUtc, 1)[0],
        ReportFrequency.Quarterly => Recent(ReportPeriodKind.Quarter, zone, nowUtc, 1)[0],
        _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "A schedule that's off has no period."),
    };

    /// <summary>The last <paramref name="count"/> complete periods of a kind, newest first, for the period picker.</summary>
    public static IReadOnlyList<ReportPeriod> Recent(ReportPeriodKind kind, TimeZoneInfo zone, DateTimeOffset nowUtc, int count)
    {
        var today = LocalDay(nowUtc, zone);
        var current = kind switch
        {
            ReportPeriodKind.Week => Week(today.AddDays(-(((int)today.DayOfWeek + 6) % 7))),
            ReportPeriodKind.Month => Month(new DateOnly(today.Year, today.Month, 1)),
            ReportPeriodKind.Quarter => Quarter(new DateOnly(today.Year, (((today.Month - 1) / 3) * 3) + 1, 1)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Custom ranges aren't listed."),
        };
        var periods = new List<ReportPeriod>();
        for (var step = 1; step <= count; step++)
        {
            periods.Add(kind switch
            {
                ReportPeriodKind.Week => Week(current.Start.AddDays(-7 * step)),
                ReportPeriodKind.Month => Month(current.Start.AddMonths(-step)),
                _ => Quarter(current.Start.AddMonths(-3 * step)),
            });
        }

        return periods;
    }

    /// <summary>A range picked by hand, recognised as a week, month or quarter when it is exactly one.</summary>
    public static ReportPeriod FromRange(DateOnly start, DateOnly end, TimeZoneInfo zone, DateTimeOffset nowUtc)
    {
        if (start > end) throw new ArgumentException("The start date must be on or before the end date.", nameof(start));
        if (end >= LocalDay(nowUtc, zone)) throw new ArgumentException("A report can only cover days that have ended.", nameof(end));
        if (end.DayNumber - start.DayNumber + 1 > MaximumDays) throw new ArgumentException($"A report can cover at most {MaximumDays} days.", nameof(end));

        if (start.DayOfWeek == DayOfWeek.Monday && end == start.AddDays(6)) return Week(start);
        if (start.Day == 1 && end == start.AddMonths(1).AddDays(-1)) return Month(start);
        if (start.Day == 1 && (start.Month - 1) % 3 == 0 && end == start.AddMonths(3).AddDays(-1)) return Quarter(start);
        return new ReportPeriod(start, end, ReportPeriodKind.Custom);
    }

    /// <summary>What a period's figures are compared with: the previous week, month or quarter, or for a custom range
    /// the equal-length range just before it.</summary>
    public static ReportPeriod PreviousForComparison(ReportPeriod period) => period.Kind switch
    {
        ReportPeriodKind.Week => Week(period.Start.AddDays(-7)),
        ReportPeriodKind.Month => Month(period.Start.AddMonths(-1)),
        ReportPeriodKind.Quarter => Quarter(period.Start.AddMonths(-3)),
        _ => new ReportPeriod(period.Start.AddDays(-period.Days), period.Start.AddDays(-1), ReportPeriodKind.Custom),
    };

    public static DateTimeOffset StartUtc(ReportPeriod period, TimeZoneInfo zone) => LocalTimeToUtc(period.Start, 0, zone);

    /// <summary>The instant just after the period, exclusive.</summary>
    public static DateTimeOffset EndUtc(ReportPeriod period, TimeZoneInfo zone) => LocalTimeToUtc(period.End.AddDays(1), 0, zone);

    public static DateTimeOffset DueUtc(ReportPeriod period, TimeZoneInfo zone, int sendHour) => LocalTimeToUtc(period.End.AddDays(1), sendHour, zone);

    /// <summary>A local day and hour as a UTC instant. A local time skipped by a clock change becomes the first valid
    /// minute after it; one that happens twice takes the first occurrence.</summary>
    private static DateTimeOffset LocalTimeToUtc(DateOnly day, int hour, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
```

(In `ASendHourThatDoesntExist_BecomesTheFirstValidTimeAfterIt` the first valid local minute after 01:00 is 02:00 BST, which is 01:00 UTC.)

```csharp
// src/DotMarc/Reporting/ClientReports/ReportSettings.cs
namespace DotMarc.Reporting.ClientReports;

/// <summary>Singleton row (seeded <c>Id = 1</c>): the time zone report periods are measured in, and the local hour
/// scheduled reports go out.</summary>
public sealed class ReportSettings
{
    public int Id { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public int SendHour { get; set; } = 6;
}
```

```csharp
// src/DotMarc/Reporting/ClientReports/ReportSettingsService.cs
using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Reporting.ClientReports;

public static class ReportSettingsService
{
    public static Task<ReportSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.ReportSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, ReportSettings updated, CancellationToken cancellationToken = default)
    {
        updated.TimeZoneId = updated.TimeZoneId?.Trim() ?? "";
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(updated.TimeZoneId, out _)) throw new ArgumentException($"{updated.TimeZoneId} isn't a time zone this server knows.", nameof(updated));
        if (updated.SendHour is < 0 or > 23) throw new ArgumentException("Send hour must be between 0 and 23.", nameof(updated));

        var saved = await context.ReportSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges()
            .Field("Time zone", saved.TimeZoneId, updated.TimeZoneId)
            .Field("Send hour", saved.SendHour, updated.SendHour);
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.ReportSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.TimeZoneId = updated.TimeZoneId;
        existing.SendHour = updated.SendHour;
        AuditLog.Record(context, actor, AuditActions.ReportSettingsSaved, AuditTarget.Settings("Reports"), "Saved report settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The saved zone, or UTC when this server doesn't know it (for example after moving to another OS).</summary>
    public static TimeZoneInfo ResolveZone(string timeZoneId, ILogger? logger = null)
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone))
        {
            return zone;
        }

        logger?.LogWarning("The report time zone {TimeZoneId} isn't known on this server, so reports use UTC.", timeZoneId);
        return TimeZoneInfo.Utc;
    }

    /// <summary>IANA zone ids for the picker, whatever the server's own naming (Windows ids are converted).</summary>
    public static IReadOnlyList<string> AvailableZones() =>
        TimeZoneInfo.GetSystemTimeZones()
            .Select(zone => zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var ianaId) ? ianaId : null)
            .OfType<string>()
            .Append("UTC")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
}
```

```csharp
// src/DotMarc/Reporting/ClientReports/GroupReportSchedule.cs
namespace DotMarc.Reporting.ClientReports;

/// <summary>A Group's report schedule. <see cref="StartedUtc"/> is when the current frequency was set: periods that
/// fell due before it aren't sent, so turning a schedule on never sends a backlog.</summary>
public sealed class GroupReportSchedule
{
    public int GroupId { get; set; }
    public ReportFrequency Frequency { get; set; }
    public List<string> Recipients { get; set; } = [];
    public DateTimeOffset StartedUtc { get; set; }
}
```

```csharp
// src/DotMarc/Reporting/ClientReports/ClientReportDelivery.cs
namespace DotMarc.Reporting.ClientReports;

public enum ClientReportDeliveryKind { Scheduled, Manual }

public enum ClientReportDeliveryStatus { Pending, Sent, Failed, Skipped }

/// <summary>One report sent, or tried, for a Group and period. A scheduled period has at most one row (a unique index),
/// which is how it's sent only once; manual sends get a row each and never affect the schedule.</summary>
public sealed class ClientReportDelivery
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public ClientReportDeliveryKind Kind { get; set; }
    public List<string> Recipients { get; set; } = [];

    /// <summary>Who sent a manual report.</summary>
    public string? RequestedBy { get; set; }

    public ClientReportDeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? FirstAttemptUtc { get; set; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public DateTimeOffset? SentUtc { get; set; }

    /// <summary>The last failure, or why the period was skipped.</summary>
    public string? Error { get; set; }
}
```

```csharp
// src/DotMarc/Reporting/ClientReports/ClientReportAccess.cs
using System.Security.Claims;
using DotMarc.Portal;

namespace DotMarc.Reporting.ClientReports;

public static class ClientReportAccess
{
    /// <summary>Staff limited to some Groups manage reports only for those; unscoped staff for any Group. The same rule
    /// as previewing a Group's portal. The ReportsManage permission is checked separately, by policy.</summary>
    public static bool MayManage(ClaimsPrincipal user, int groupId) =>
        !ClientPortalGate.IsPortalUser(user) && PortalScope.CanPreview(user, groupId);
}
```

```csharp
// src/DotMarc/Reporting/ClientReports/ClientReportService.cs
using System.Net.Mail;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Email;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Reporting.ClientReports;

/// <summary>A Group's report schedule and delivery history.</summary>
public static class ClientReportService
{
    public static Task<GroupReportSchedule?> GetScheduleAsync(DotMarcDbContext context, int groupId, CancellationToken cancellationToken = default) =>
        context.GroupReportSchedules.AsNoTracking().SingleOrDefaultAsync(schedule => schedule.GroupId == groupId, cancellationToken);

    public static async Task SetScheduleAsync(DotMarcDbContext context, AuditActor actor, int groupId, ReportFrequency frequency, IReadOnlyList<string> recipients, CancellationToken cancellationToken = default)
    {
        var tidied = NormaliseRecipients(recipients);
        if (frequency != ReportFrequency.Off && tidied.Count == 0)
        {
            throw new ArgumentException("Add at least one recipient, or turn the schedule off.", nameof(recipients));
        }

        var group = await context.Groups.SingleAsync(candidate => candidate.Id == groupId, cancellationToken).ConfigureAwait(false);
        var existing = await context.GroupReportSchedules.SingleOrDefaultAsync(schedule => schedule.GroupId == groupId, cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges()
            .Field("Frequency", existing?.Frequency ?? ReportFrequency.Off, frequency)
            .Set("Recipients", existing?.Recipients ?? [], tidied);
        if (!changes.Any)
        {
            return;
        }

        if (frequency == ReportFrequency.Off && tidied.Count == 0)
        {
            if (existing is not null)
            {
                context.GroupReportSchedules.Remove(existing);
            }
        }
        else
        {
            if (existing is null)
            {
                existing = new GroupReportSchedule { GroupId = groupId };
                context.GroupReportSchedules.Add(existing);
            }

            if (existing.Frequency != frequency || existing.StartedUtc == default)
            {
                existing.StartedUtc = DateTimeOffset.UtcNow;
            }

            existing.Frequency = frequency;
            existing.Recipients = tidied;
        }

        AuditLog.Record(context, actor, AuditActions.GroupReportScheduleChanged, AuditTarget.For(group), $"Changed the report schedule for group {group.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Trimmed, lower-cased, de-duplicated and sorted; throws for an invalid address or too many.</summary>
    public static List<string> NormaliseRecipients(IEnumerable<string> recipients)
    {
        var tidied = recipients
            .Select(recipient => recipient.Trim().ToLowerInvariant())
            .Where(recipient => recipient.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        foreach (var recipient in tidied)
        {
            if (recipient.Length > 254 || !MailAddress.TryCreate(recipient, out var parsed) || parsed.Address != recipient)
            {
                throw new ArgumentException($"{recipient} isn't a valid email address.", nameof(recipients));
            }
        }

        if (tidied.Count > EmailLimits.MaximumRecipients)
        {
            throw new ArgumentException($"A report can go to at most {EmailLimits.MaximumRecipients} recipients.", nameof(recipients));
        }

        return tidied;
    }

    /// <summary>Emails of the grants limited to this Group (its client contacts and Group-scoped staff), for the
    /// recipient field's suggestions.</summary>
    public static async Task<IReadOnlyList<string>> SuggestRecipientsAsync(DotMarcDbContext context, int groupId, CancellationToken cancellationToken = default) =>
        (await context.UserAccesses.AsNoTracking()
            .Where(access => access.ScopedGroups.Any(group => group.Id == groupId))
            .Select(access => access.Email)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        .Select(email => email.ToLowerInvariant())
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToList();

    public static async Task<IReadOnlyList<ClientReportDelivery>> ListDeliveriesAsync(DotMarcDbContext context, int groupId, int count, CancellationToken cancellationToken = default) =>
        await context.ClientReportDeliveries.AsNoTracking()
            .Where(delivery => delivery.GroupId == groupId)
            .OrderByDescending(delivery => delivery.LastAttemptUtc ?? delivery.SentUtc)
            .ThenByDescending(delivery => delivery.Id)
            .Take(count)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}
```

`Permission.cs`: add `ReportsManage` at the end of the enum.

`DotMarcDbContext`: sets `ReportSettings`, `GroupReportSchedules`, `ClientReportDeliveries`, and:

```csharp
modelBuilder.Entity<DotMarc.Reporting.ClientReports.ReportSettings>(entity =>
{
    entity.Property(settings => settings.TimeZoneId).HasMaxLength(64);
    entity.HasData(new DotMarc.Reporting.ClientReports.ReportSettings { Id = 1 });
});
modelBuilder.Entity<DotMarc.Reporting.ClientReports.GroupReportSchedule>(entity =>
{
    entity.HasKey(schedule => schedule.GroupId);
    entity.HasOne<Group>().WithOne().HasForeignKey<DotMarc.Reporting.ClientReports.GroupReportSchedule>(schedule => schedule.GroupId).OnDelete(DeleteBehavior.Cascade);
    entity.Property(schedule => schedule.Frequency).HasConversion<string>().HasMaxLength(10);
});
modelBuilder.Entity<DotMarc.Reporting.ClientReports.ClientReportDelivery>(entity =>
{
    entity.HasOne<Group>().WithMany().HasForeignKey(delivery => delivery.GroupId).OnDelete(DeleteBehavior.Cascade);
    entity.Property(delivery => delivery.Kind).HasConversion<string>().HasMaxLength(10);
    entity.Property(delivery => delivery.Status).HasConversion<string>().HasMaxLength(10);
    entity.Property(delivery => delivery.RequestedBy).HasMaxLength(254);
    entity.Property(delivery => delivery.Error).HasMaxLength(1000);
    entity.HasIndex(delivery => new { delivery.GroupId, delivery.PeriodStart, delivery.PeriodEnd })
        .IsUnique()
        .HasFilter("\"Kind\" = 'Scheduled'");
});
```

`AuditActions`: the three constants and labels. `AuditCoverageTests`: add `typeof(DotMarc.Reporting.ClientReports.ReportSettingsService), typeof(DotMarc.Reporting.ClientReports.ClientReportService)`. `DemoDataSeeder` truncate list: add `"GroupReportSchedules", "ClientReportDeliveries"`.

Run: `dotnet ef migrations add AddClientReports --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj`

- [ ] **Step 4: Run the tests and commit**

Run the Step 2 filter (PASS), then `dotnet test test/DotMarc.Tests`.

```bash
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Add report periods, settings and per-Group schedules"
```

---

### Task 3: The report model and its calculations

**Files:**
- Create: `src/DotMarc/Portal/PortalWording.cs`, `src/DotMarc/Reporting/ClientReports/ClientReport.cs`, `src/DotMarc/Reporting/ClientReports/ClientReportCalculator.cs`, `src/DotMarc/Reporting/ClientReports/ClientReportNextSteps.cs`, `src/DotMarc/Reporting/ClientReports/ClientReportBuilder.cs`
- Modify: `src/DotMarc/Components/Portal/PortalDomainView.razor` (use `PortalWording`), `src/DotMarc/Reporting/DomainStatistics.cs` (`IsPassing` public), `src/DotMarc/Program.cs` (register the builder)
- Test: `test/DotMarc.Tests/Reporting/ClientReports/ClientReportCalculatorTests.cs`, `test/DotMarc.Tests/Reporting/ClientReports/ClientReportNextStepsTests.cs`, `test/DotMarc.Tests/Reporting/ClientReports/ClientReportBuilderTests.cs`, `test/DotMarc.Tests/Portal/PortalWordingTests.cs`

**Interfaces:**
- Consumes: `ReportPeriod`, `ReportPeriods` (Task 2); `PortalStatus`, `ResolvedBrand`, `PortalBrandLoader` (existing).
- Produces:
  - `PortalWording.PolicySentence(Domain) : string`, `PortalWording.HealthRows(Domain) : IReadOnlyList<HealthRow>`, `record HealthRow(string Name, string Label, MudBlazor.Color Colour)`
  - `record ClientReport(ResolvedBrand Brand, byte[]? Logo, string GroupName, ReportPeriod Period, string TimeZoneId, string Verdict, IReadOnlyList<ClientReportDomain> Domains, IReadOnlyList<string> NextSteps, DateTimeOffset GeneratedUtc)`
  - `record ClientReportDomain(string Name, PortalDomainStatus Status, long Messages, double? PassRate, double? PreviousPassRate, IReadOnlyList<double?> Trend, string PolicySentence, IReadOnlyList<HealthRow> Health, IReadOnlyList<ClientReportSender> TopSenders, ReceiverActions Receivers, IReadOnlyList<ClientReportAlert> Alerts)` with `string ChangeText`
  - `record ClientReportSender(string Ip, string? Owner, long Messages, long Passing, long Failing, double Share)`, `record ReceiverActions(long Delivered, long Quarantined, long Rejected, long FailingDelivered)`, `record ClientReportAlert(string Title, DateTimeOffset CreatedUtc, DateTimeOffset? ResolvedUtc)`
  - `ClientReportCalculator.Build(ClientReportInputs) : ClientReport` with `record ClientReportInputs(ResolvedBrand Brand, byte[]? Logo, string GroupName, ReportPeriod Period, TimeZoneInfo Zone, IReadOnlyList<Domain> Domains, IReadOnlyList<Report> Reports, IReadOnlyList<AlertEvent> PeriodAlerts, IReadOnlyList<AlertEvent> OpenAlerts, IReadOnlyDictionary<string, IpInfo> Owners, DateTimeOffset NowUtc)`
  - `ClientReportNextSteps.For(IReadOnlyList<Domain> domains, IReadOnlyList<ClientReportDomain> reportDomains) : IReadOnlyList<string>`, `ClientReportNextSteps.NothingToDo`
  - `ClientReportBuilder(IDbContextFactory<DotMarcDbContext>, PortalBrandLoader, TimeProvider)` scoped, `Task<ClientReport?> BuildAsync(int groupId, ReportPeriod period, TimeZoneInfo zone, CancellationToken)` (null when the Group doesn't exist)

- [ ] **Step 1: Write the failing tests**

```csharp
// test/DotMarc.Tests/Portal/PortalWordingTests.cs
using DotMarc.Data;
using DotMarc.Portal;
using Xunit;

namespace DotMarc.Tests.Portal;

public sealed class PortalWordingTests
{
    [Theory]
    [InlineData(null, null, null, "No DMARC policy was found, so receivers decide for themselves what to do with mail that fails.")]
    [InlineData(DmarcPolicyLevel.Reject, null, null, "Mail that fails DMARC is rejected.")]
    [InlineData(DmarcPolicyLevel.Quarantine, 50, DmarcPolicyLevel.Reject, "Mail that fails DMARC is sent to spam (50% of it). Subdomains: rejected.")]
    public void ThePolicy_IsSaidInPlainWords(DmarcPolicyLevel? policy, int? percent, DmarcPolicyLevel? subdomainPolicy, string expected)
    {
        var domain = new Domain { Name = "aurora-retail.example", DmarcPolicy = policy, DmarcPercent = percent, DmarcSubdomainPolicy = subdomainPolicy };

        Assert.Equal(expected, PortalWording.PolicySentence(domain));
    }

    [Fact]
    public void MtaSts_IsOnlyListedWhenSetUp()
    {
        var domain = new Domain { Name = "aurora-retail.example", MtaStsStatus = MtaStsStatus.NotConfigured };

        Assert.DoesNotContain(PortalWording.HealthRows(domain), row => row.Name == "MTA-STS");
    }
}
```

(Check `Domain`'s required members; set whatever else `new Domain { ... }` requires.)

```csharp
// test/DotMarc.Tests/Reporting/ClientReports/ClientReportCalculatorTests.cs
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Portal;
using DotMarc.Reporting;
using DotMarc.Reporting.ClientReports;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

public sealed class ClientReportCalculatorTests
{
    private static readonly ReportPeriod March = new(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), ReportPeriodKind.Month);
    private static readonly ResolvedBrand Brand = new("Nova MSP", "Aurora Retail Ltd", "#0B5FFF", "#FF6B00", null, null, null, null, null, null);
    private static readonly DateTimeOffset Now = new(2026, 4, 1, 6, 0, 0, TimeSpan.Zero);

    private static Domain AuroraDomain(int id = 1, string name = "aurora-retail.example") => new()
    {
        Id = id, Name = name, IsMonitored = true, DmarcPolicy = DmarcPolicyLevel.Reject, LastReportReceivedUtc = Now,
        DmarcCheckStatus = DmarcCheckStatus.Ok, SpfCheckStatus = SpfCheckStatus.Ok, MxCheckStatus = MxCheckStatus.Ok,
        DkimCheckStatus = DkimCheckStatus.Ok, TlsrptCheckStatus = TlsrptCheckStatus.Ok,
    };

    private static Report ReportOn(int domainId, DateTimeOffset beginUtc, params ReportRecord[] records) => new()
    {
        DomainId = domainId, ReportingOrg = "google.com", ReportId = Guid.NewGuid().ToString(), DateRangeBeginUtc = beginUtc,
        DateRangeEndUtc = beginUtc.AddDays(1), ReceivedUtc = beginUtc.AddDays(1), Records = records.ToList(), RawXml = "<feedback/>",
    };

    private static ReportRecord Record(string ip, int count, AuthResult spf, AuthResult dkim, DispositionResult disposition = DispositionResult.None) => new()
    {
        SourceIp = ip, MessageCount = count, SpfResult = spf, DkimResult = dkim, Disposition = disposition, HeaderFrom = "aurora-retail.example",
    };

    private static ClientReport Build(IReadOnlyList<Domain> domains, IReadOnlyList<Report> reports, ReportPeriod? period = null, TimeZoneInfo? zone = null,
        IReadOnlyList<AlertEvent>? periodAlerts = null, IReadOnlyDictionary<string, IpInfo>? owners = null) =>
        ClientReportCalculator.Build(new ClientReportInputs(Brand, null, "Aurora Retail", period ?? March, zone ?? TimeZoneInfo.Utc, domains, reports,
            periodAlerts ?? [], [], owners ?? new Dictionary<string, IpInfo>(), Now));

    [Fact]
    public void ThePassRate_MatchesTheDashboardsDefinition()
    {
        var domain = AuroraDomain();
        var reports = new[]
        {
            ReportOn(1, new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero),
                Record("203.0.113.10", 90, AuthResult.Pass, AuthResult.Fail), Record("198.51.100.7", 10, AuthResult.Fail, AuthResult.Fail)),
        };

        var report = Build([domain], reports);

        var row = Assert.Single(report.Domains);
        Assert.Equal((100L, DomainStatistics.GetPassRate(reports)), (row.Messages, row.PassRate));
    }

    [Fact]
    public void TheChange_IsAgainstThePreviousMonth_OrNewWhenThereWasNothingBefore()
    {
        var domain = AuroraDomain();
        var february = ReportOn(1, new DateTimeOffset(2026, 2, 10, 0, 0, 0, TimeSpan.Zero), Record("203.0.113.10", 80, AuthResult.Pass, AuthResult.Pass), Record("198.51.100.7", 20, AuthResult.Fail, AuthResult.Fail));
        var march = ReportOn(1, new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero), Record("203.0.113.10", 90, AuthResult.Pass, AuthResult.Pass), Record("198.51.100.7", 10, AuthResult.Fail, AuthResult.Fail));

        Assert.Equal("+10.0 pts", Build([domain], [february, march]).Domains.Single().ChangeText);
        Assert.Equal("new", Build([domain], [march]).Domains.Single().ChangeText);
    }

    [Fact]
    public void AReportStraddlingLocalMidnight_CountsInTheLocalDaysPeriod()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var domain = AuroraDomain();
        // 23:30 UTC on 28 February is 00:30 on 1 March in Berlin.
        var lateFebruaryUtc = ReportOn(1, new DateTimeOffset(2026, 2, 28, 23, 30, 0, TimeSpan.Zero), Record("203.0.113.10", 50, AuthResult.Pass, AuthResult.Pass));

        Assert.Equal(50L, Build([domain], [lateFebruaryUtc], zone: berlin).Domains.Single().Messages);
        Assert.Equal(0L, Build([domain], [lateFebruaryUtc], zone: TimeZoneInfo.Utc).Domains.Single().Messages);
    }

    [Fact]
    public void TopSenders_AreByVolume_WithTheirOwnersAndPassAndFailCounts()
    {
        var domain = AuroraDomain();
        var reports = new[]
        {
            ReportOn(1, new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero),
                Record("203.0.113.10", 70, AuthResult.Pass, AuthResult.Pass),
                Record("198.51.100.7", 25, AuthResult.Fail, AuthResult.Fail),
                Record("198.51.100.7", 5, AuthResult.Pass, AuthResult.Fail)),
        };
        var owners = new Dictionary<string, IpInfo> { ["198.51.100.7"] = new() { Ip = "198.51.100.7", Organization = "Mailchimp" } };

        var senders = Build([domain], reports, owners: owners).Domains.Single().TopSenders;

        Assert.Equal(new ClientReportSender("203.0.113.10", null, 70, 70, 0, 0.7), senders[0]);
        Assert.Equal(new ClientReportSender("198.51.100.7", "Mailchimp", 30, 5, 25, 0.3), senders[1]);
    }

    [Fact]
    public void ReceiverActions_CountWhatHappened_IncludingFailingMailDelivered()
    {
        var domain = AuroraDomain();
        var reports = new[]
        {
            ReportOn(1, new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero),
                Record("203.0.113.10", 60, AuthResult.Pass, AuthResult.Pass),
                Record("198.51.100.7", 15, AuthResult.Fail, AuthResult.Fail),
                Record("198.51.100.8", 20, AuthResult.Fail, AuthResult.Fail, DispositionResult.Quarantine),
                Record("198.51.100.9", 5, AuthResult.Fail, AuthResult.Fail, DispositionResult.Reject)),
        };

        Assert.Equal(new ReceiverActions(75, 20, 5, 15), Build([domain], reports).Domains.Single().Receivers);
    }

    [Fact]
    public void TheTrend_HasAPointPerLocalDay_WithGapsWhereThereWasNoData()
    {
        var domain = AuroraDomain();
        var reports = new[] { ReportOn(1, new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), Record("203.0.113.10", 10, AuthResult.Pass, AuthResult.Pass)) };

        var trend = Build([domain], reports).Domains.Single().Trend;

        Assert.Equal(31, trend.Count);
        Assert.Null(trend[0]);
        Assert.Equal(1.0, trend[1]);
    }

    [Fact]
    public void ALongRange_HasWeeklyTrendPoints()
    {
        var domain = AuroraDomain();
        var halfYear = new ReportPeriod(new DateOnly(2025, 10, 1), new DateOnly(2026, 3, 31), ReportPeriodKind.Custom);

        Assert.Equal(26, Build([domain], [], period: halfYear).Domains.Single().Trend.Count); // 182 days in 7-day buckets, rounded up
    }

    [Fact]
    public void Alerts_RaisedOrResolvedInThePeriod_AreListed()
    {
        var domain = AuroraDomain();
        var alerts = new[]
        {
            new AlertEvent { DomainName = "aurora-retail.example", AlertType = AlertTypes.SpfRecordBroken, Severity = "Warning", Title = "SPF record broken", Message = "", CreatedUtc = new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero) },
        };

        Assert.Equal("SPF record broken", Build([domain], [], periodAlerts: alerts).Domains.Single().Alerts.Single().Title);
    }

    [Fact]
    public void TheVerdict_IsThePortals()
    {
        var report = Build([AuroraDomain(), AuroraDomain(2, "shop.aurora-retail.example")], []);

        Assert.Equal(PortalStatus.Verdict(report.Domains.Select(domain => domain.Status).ToList()), report.Verdict);
    }
}
```

(Check `Report`'s required members (`RawXml` may be on a separate `ReportRawXml` row); construct whatever the entity requires.)

```csharp
// test/DotMarc.Tests/Reporting/ClientReports/ClientReportNextStepsTests.cs
using DotMarc.Data;
using DotMarc.Portal;
using DotMarc.Reporting.ClientReports;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

public sealed class ClientReportNextStepsTests
{
    private static Domain Healthy(string name = "aurora-retail.example", DmarcPolicyLevel policy = DmarcPolicyLevel.Reject) => new()
    {
        Name = name, IsMonitored = true, DmarcPolicy = policy,
        DmarcCheckStatus = DmarcCheckStatus.Ok, SpfCheckStatus = SpfCheckStatus.Ok, MxCheckStatus = MxCheckStatus.Ok,
        DkimCheckStatus = DkimCheckStatus.Ok, TlsrptCheckStatus = TlsrptCheckStatus.Ok,
    };

    private static ClientReportDomain Row(string name, long messages, params ClientReportSender[] senders) => new(
        name, new PortalDomainStatus(PortalHealth.Protected, []), messages, 1.0, null, [], "", [], senders, new ReceiverActions(messages, 0, 0, 0), []);

    [Fact]
    public void AnAllProtectedGroup_HasNothingToDo()
    {
        Assert.Equal([ClientReportNextSteps.NothingToDo], ClientReportNextSteps.For([Healthy()], [Row("aurora-retail.example", 100)]));
    }

    [Fact]
    public void AMonitoringOnlyPolicy_IsToldToMoveToQuarantine_AndQuarantineToReject()
    {
        var steps = ClientReportNextSteps.For(
            [Healthy("aurora-retail.example", DmarcPolicyLevel.None), Healthy("shop.aurora-retail.example", DmarcPolicyLevel.Quarantine)],
            [Row("aurora-retail.example", 100), Row("shop.aurora-retail.example", 100)]);

        Assert.Equal(
        [
            "Move aurora-retail.example from monitoring to a quarantine policy, so mail that fails DMARC is sent to spam.",
            "When you're ready, move shop.aurora-retail.example to a reject policy.",
        ], steps);
    }

    [Fact]
    public void AFailingCheck_GetsItsOwnSentence()
    {
        var domain = Healthy();
        domain.SpfCheckStatus = SpfCheckStatus.MissingRecord;

        Assert.Contains("Publish an SPF record for aurora-retail.example.", ClientReportNextSteps.For([domain], [Row("aurora-retail.example", 100)]));
    }

    [Fact]
    public void AKnownSenderFailingOver5Percent_IsNamed_ButNotAnUnknownOne()
    {
        var senders = new[]
        {
            new ClientReportSender("198.51.100.7", "Mailchimp", 60, 0, 60, 0.06),
            new ClientReportSender("198.51.100.8", null, 90, 0, 90, 0.09),
            new ClientReportSender("198.51.100.9", "Zendesk", 40, 0, 40, 0.04),
        };

        var steps = ClientReportNextSteps.For([Healthy()], [Row("aurora-retail.example", 1000, senders)]);

        Assert.Equal(["Mailchimp sent 60 messages as aurora-retail.example that failed DMARC. If they send for you, add them to SPF or set up DKIM for them."], steps);
    }

    [Fact]
    public void ADomainWithNoReports_IsToldToCheckItsReportingAddress()
    {
        Assert.Equal(["No DMARC reports arrived for aurora-retail.example. Check its DMARC record's reporting address."],
            ClientReportNextSteps.For([Healthy()], [Row("aurora-retail.example", 0)]));
    }
}
```

```csharp
// test/DotMarc.Tests/Reporting/ClientReports/ClientReportBuilderTests.cs
using DotMarc.Data;
using DotMarc.Portal;
using DotMarc.Reporting.ClientReports;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

/// <summary>The builder's queries: only the Group's monitored domains, only the period's reports.</summary>
[Collection("Postgres")]
public sealed class ClientReportBuilderTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ClientReportBuilderTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    [Fact]
    public async Task TheReport_CoversTheGroupsMonitoredDomains_AndOnlyThePeriodsReports()
    {
        int groupId;
        await using (var context = CreateContext())
        {
            var aurora = new Group { Name = "Aurora Retail" };
            var other = new Group { Name = "Brightline Legal" };
            var inGroup = new Domain { Name = "aurora-retail.example", IsMonitored = true, Groups = [aurora] };
            var unmonitored = new Domain { Name = "old.aurora-retail.example", IsMonitored = false, Groups = [aurora] };
            var elsewhere = new Domain { Name = "brightline-legal.example", IsMonitored = true, Groups = [other] };
            context.Domains.AddRange(inGroup, unmonitored, elsewhere);
            await context.SaveChangesAsync();
            context.Reports.AddRange(
                Report(inGroup.Id, new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero), 40),
                Report(inGroup.Id, new DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero), 999));
            await context.SaveChangesAsync();
            groupId = aurora.Id;
        }

        var builder = new ClientReportBuilder(new FakeDbContextFactory(_connectionString), new PortalBrandLoader(new FakeDbContextFactory(_connectionString)),
            new FixedTimeProvider(new DateTimeOffset(2026, 4, 3, 0, 0, 0, TimeSpan.Zero)));
        var report = await builder.BuildAsync(groupId, new ReportPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), ReportPeriodKind.Month), TimeZoneInfo.Utc, CancellationToken.None);

        var domain = Assert.Single(report!.Domains);
        Assert.Equal(("aurora-retail.example", 40L, "Aurora Retail"), (domain.Name, domain.Messages, report.Brand.Heading));
    }

    [Fact]
    public async Task AGroupThatDoesntExist_HasNoReport()
    {
        var builder = new ClientReportBuilder(new FakeDbContextFactory(_connectionString), new PortalBrandLoader(new FakeDbContextFactory(_connectionString)), TimeProvider.System);

        Assert.Null(await builder.BuildAsync(424242, new ReportPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), ReportPeriodKind.Month), TimeZoneInfo.Utc, CancellationToken.None));
    }

    private static Report Report(int domainId, DateTimeOffset beginUtc, int messages) => new()
    {
        DomainId = domainId, ReportingOrg = "google.com", ReportId = Guid.NewGuid().ToString(), DateRangeBeginUtc = beginUtc,
        DateRangeEndUtc = beginUtc.AddDays(1), ReceivedUtc = beginUtc.AddDays(1), RawXml = "<feedback/>",
        Records = [new ReportRecord { SourceIp = "203.0.113.10", MessageCount = messages, SpfResult = AuthResult.Pass, DkimResult = AuthResult.Pass, HeaderFrom = "aurora-retail.example" }],
    };
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ClientReportCalculatorTests|FullyQualifiedName~ClientReportNextStepsTests|FullyQualifiedName~ClientReportBuilderTests|FullyQualifiedName~PortalWordingTests"`
Expected: build FAIL.

- [ ] **Step 3: Implement**

Move the portal's wording out of `PortalDomainView.razor` into `PortalWording` (same text), and make the view call it:

```csharp
// src/DotMarc/Portal/PortalWording.cs
using System.Globalization;
using DotMarc.Data;
using DotMarc.Reporting;
using MudBlazor;

namespace DotMarc.Portal;

public sealed record HealthRow(string Name, string Label, Color Colour);

/// <summary>How the portal and client reports describe a domain to clients, so both say it the same way.</summary>
public static class PortalWording
{
    public static IReadOnlyList<HealthRow> HealthRows(Domain domain)
    {
        var rows = new List<HealthRow>
        {
            new("DMARC", DmarcStatusPresentation.GetLabel(domain.DmarcCheckStatus), DmarcStatusPresentation.GetColor(domain.DmarcCheckStatus)),
            new("DMARC reporting", DmarcAuthorizationStatusPresentation.GetLabel(domain.DmarcAuthorizationCheckStatus), DmarcAuthorizationStatusPresentation.GetColor(domain.DmarcAuthorizationCheckStatus)),
            new("SPF", SpfStatusPresentation.GetLabel(domain.SpfCheckStatus), SpfStatusPresentation.GetColor(domain.SpfCheckStatus)),
            new("MX", MxStatusPresentation.GetLabel(domain.MxCheckStatus), MxStatusPresentation.GetColor(domain.MxCheckStatus)),
            new("DKIM", DkimStatusPresentation.GetLabel(domain.DkimCheckStatus), DkimStatusPresentation.GetColor(domain.DkimCheckStatus)),
            new("TLS reporting", TlsrptStatusPresentation.GetLabel(domain.TlsrptCheckStatus), TlsrptStatusPresentation.GetColor(domain.TlsrptCheckStatus)),
        };
        if (domain.MtaStsStatus != MtaStsStatus.NotConfigured)
        {
            rows.Add(new("MTA-STS", MtaStsStatusPresentation.GetLabel(domain.MtaStsStatus), MtaStsStatusPresentation.GetColor(domain.MtaStsStatus)));
        }

        return rows;
    }

    public static string PolicySentence(Domain domain)
    {
        if (domain.DmarcPolicy is not { } policy)
        {
            return "No DMARC policy was found, so receivers decide for themselves what to do with mail that fails.";
        }

        var main = policy switch
        {
            DmarcPolicyLevel.Reject => "Mail that fails DMARC is rejected",
            DmarcPolicyLevel.Quarantine => "Mail that fails DMARC is sent to spam",
            _ => "Mail that fails DMARC is only reported, not blocked",
        };
        var percent = policy != DmarcPolicyLevel.None && domain.DmarcPercent is { } share && share < 100
            ? string.Create(CultureInfo.InvariantCulture, $" ({share}% of it)")
            : "";
        var subdomains = domain.DmarcSubdomainPolicy is { } subdomainPolicy && subdomainPolicy != policy
            ? subdomainPolicy switch
            {
                DmarcPolicyLevel.Reject => " Subdomains: rejected.",
                DmarcPolicyLevel.Quarantine => " Subdomains: sent to spam.",
                _ => " Subdomains: only reported.",
            }
            : "";
        return $"{main}{percent}.{subdomains}";
    }
}
```

In `PortalDomainView.razor`, replace `HealthRows(domain)` and `PolicySentence(domain)` with `PortalWording.HealthRows(domain)` and `PortalWording.PolicySentence(domain)` (rows use `row.Colour`), and delete the private copies and the `HealthRow` record there. `DomainStatistics.IsPassing` becomes `public static`.

```csharp
// src/DotMarc/Reporting/ClientReports/ClientReport.cs
using System.Globalization;
using DotMarc.Portal;

namespace DotMarc.Reporting.ClientReports;

public sealed record ClientReport(
    ResolvedBrand Brand, byte[]? Logo, string GroupName, ReportPeriod Period, string TimeZoneId, string Verdict,
    IReadOnlyList<ClientReportDomain> Domains, IReadOnlyList<string> NextSteps, DateTimeOffset GeneratedUtc);

public sealed record ClientReportDomain(
    string Name, PortalDomainStatus Status, long Messages, double? PassRate, double? PreviousPassRate, IReadOnlyList<double?> Trend,
    string PolicySentence, IReadOnlyList<HealthRow> Health, IReadOnlyList<ClientReportSender> TopSenders, ReceiverActions Receivers,
    IReadOnlyList<ClientReportAlert> Alerts)
{
    /// <summary>The pass rate's change from the comparison period in percentage points, "new" when that period had no
    /// mail, or empty when this one had none.</summary>
    public string ChangeText => PassRate is not { } rate
        ? ""
        : PreviousPassRate is not { } previous
            ? "new"
            : string.Create(CultureInfo.InvariantCulture, $"{(rate - previous) * 100:+0.0;-0.0;0.0} pts");
}

public sealed record ClientReportSender(string Ip, string? Owner, long Messages, long Passing, long Failing, double Share);

public sealed record ReceiverActions(long Delivered, long Quarantined, long Rejected, long FailingDelivered);

public sealed record ClientReportAlert(string Title, DateTimeOffset CreatedUtc, DateTimeOffset? ResolvedUtc);
```

```csharp
// src/DotMarc/Reporting/ClientReports/ClientReportCalculator.cs
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Portal;

namespace DotMarc.Reporting.ClientReports;

public sealed record ClientReportInputs(
    ResolvedBrand Brand, byte[]? Logo, string GroupName, ReportPeriod Period, TimeZoneInfo Zone, IReadOnlyList<Domain> Domains,
    IReadOnlyList<Report> Reports, IReadOnlyList<AlertEvent> PeriodAlerts, IReadOnlyList<AlertEvent> OpenAlerts,
    IReadOnlyDictionary<string, IpInfo> Owners, DateTimeOffset NowUtc);

/// <summary>Turns loaded rows into a report. Pure, so every figure can be tested without a database. A report belongs to
/// the local day its date range begins, the same rule as the portal's trend, measured in the report time zone.</summary>
public static class ClientReportCalculator
{
    public const int TopSenderCount = 10;
    private const int DailyTrendLimitDays = 92;

    public static ClientReport Build(ClientReportInputs inputs)
    {
        var comparison = ReportPeriods.PreviousForComparison(inputs.Period);
        var byDomain = inputs.Reports.ToLookup(report => report.DomainId);
        var domains = inputs.Domains
            .OrderBy(domain => domain.Name, StringComparer.OrdinalIgnoreCase)
            .Select(domain =>
            {
                var reports = byDomain[domain.Id].ToList();
                var inPeriod = reports.Where(report => Covers(inputs.Period, report, inputs.Zone)).ToList();
                var inComparison = reports.Where(report => Covers(comparison, report, inputs.Zone)).ToList();
                var openAlerts = inputs.OpenAlerts.Where(alert => alert.DomainName == domain.Name).ToList();
                return new ClientReportDomain(
                    domain.Name,
                    PortalStatus.For(domain, domain.LastReportReceivedUtc is not null, openAlerts),
                    inPeriod.SelectMany(report => report.Records).Sum(record => (long)record.MessageCount),
                    DomainStatistics.GetPassRate(inPeriod),
                    DomainStatistics.GetPassRate(inComparison),
                    Trend(inputs.Period, inPeriod, inputs.Zone),
                    PortalWording.PolicySentence(domain),
                    PortalWording.HealthRows(domain),
                    TopSenders(inPeriod, inputs.Owners),
                    Receivers(inPeriod),
                    inputs.PeriodAlerts
                        .Where(alert => alert.DomainName == domain.Name)
                        .OrderBy(alert => alert.CreatedUtc)
                        .Select(alert => new ClientReportAlert(alert.Title, alert.CreatedUtc, alert.ResolvedUtc))
                        .ToList());
            })
            .ToList();

        return new ClientReport(
            inputs.Brand, inputs.Logo, inputs.GroupName, inputs.Period, inputs.Zone.Id,
            domains.Count == 0 ? "There are no domains in this report." : PortalStatus.Verdict(domains.Select(domain => domain.Status).ToList()),
            domains, ClientReportNextSteps.For(inputs.Domains.OrderBy(domain => domain.Name, StringComparer.OrdinalIgnoreCase).ToList(), domains),
            inputs.NowUtc);
    }

    private static bool Covers(ReportPeriod period, Report report, TimeZoneInfo zone)
    {
        var day = ReportPeriods.LocalDay(report.DateRangeBeginUtc, zone);
        return day >= period.Start && day <= period.End;
    }

    /// <summary>A pass rate per local day, or per 7-day bucket from the start for periods over 92 days; null where no
    /// mail was reported.</summary>
    private static IReadOnlyList<double?> Trend(ReportPeriod period, IReadOnlyList<Report> reports, TimeZoneInfo zone)
    {
        var bucketDays = period.Days > DailyTrendLimitDays ? 7 : 1;
        var buckets = (period.Days + bucketDays - 1) / bucketDays;
        var byBucket = reports.ToLookup(report => (ReportPeriods.LocalDay(report.DateRangeBeginUtc, zone).DayNumber - period.Start.DayNumber) / bucketDays);
        return Enumerable.Range(0, buckets).Select(bucket => DomainStatistics.GetPassRate(byBucket[bucket])).ToList();
    }

    private static IReadOnlyList<ClientReportSender> TopSenders(IReadOnlyList<Report> reports, IReadOnlyDictionary<string, IpInfo> owners)
    {
        var records = reports.SelectMany(report => report.Records).ToList();
        var total = records.Sum(record => (long)record.MessageCount);
        return records
            .GroupBy(record => record.SourceIp)
            .Select(source =>
            {
                var messages = source.Sum(record => (long)record.MessageCount);
                var passing = source.Where(DomainStatistics.IsPassing).Sum(record => (long)record.MessageCount);
                return new ClientReportSender(source.Key, owners.TryGetValue(source.Key, out var owner) ? owner.Organization : null,
                    messages, passing, messages - passing, total == 0 ? 0 : (double)messages / total);
            })
            .OrderByDescending(sender => sender.Messages)
            .ThenBy(sender => sender.Ip, StringComparer.Ordinal)
            .Take(TopSenderCount)
            .ToList();
    }

    private static ReceiverActions Receivers(IReadOnlyList<Report> reports)
    {
        var records = reports.SelectMany(report => report.Records).ToList();
        long Sum(Func<ReportRecord, bool> predicate) => records.Where(predicate).Sum(record => (long)record.MessageCount);
        return new ReceiverActions(
            Sum(record => record.Disposition == DispositionResult.None),
            Sum(record => record.Disposition == DispositionResult.Quarantine),
            Sum(record => record.Disposition == DispositionResult.Reject),
            Sum(record => record.Disposition == DispositionResult.None && !DomainStatistics.IsPassing(record)));
    }
}
```

(In `TopSenders_AreByVolume_WithTheirOwnersAndPassAndFailCounts`, shares are exact for 70/100 and 30/100; compare with a tolerance if floating point disagrees, by asserting the record fields individually with `precision: 6`.)

```csharp
// src/DotMarc/Reporting/ClientReports/ClientReportNextSteps.cs
using System.Globalization;
using DotMarc.Data;

namespace DotMarc.Reporting.ClientReports;

/// <summary>The "what to do next" list: fixed rules with fixed wording, never free text, in the order the spec gives.</summary>
public static class ClientReportNextSteps
{
    public const string NothingToDo = "Nothing to do. Every domain is protected.";
    private const double FailingSenderShare = 0.05;

    public static IReadOnlyList<string> For(IReadOnlyList<Domain> domains, IReadOnlyList<ClientReportDomain> reportDomains)
    {
        var rows = reportDomains.ToDictionary(row => row.Name, StringComparer.OrdinalIgnoreCase);
        var steps = new List<string>();

        foreach (var domain in domains.Where(domain => domain.DmarcPolicy == DmarcPolicyLevel.None))
        {
            steps.Add($"Move {domain.Name} from monitoring to a quarantine policy, so mail that fails DMARC is sent to spam.");
        }

        foreach (var domain in domains.Where(domain => domain.DmarcPolicy == DmarcPolicyLevel.Quarantine && !CheckSteps(domain).Any()))
        {
            steps.Add($"When you're ready, move {domain.Name} to a reject policy.");
        }

        steps.AddRange(domains.SelectMany(CheckSteps));

        foreach (var domain in domains)
        {
            if (!rows.TryGetValue(domain.Name, out var row) || row.Messages == 0)
            {
                continue;
            }

            foreach (var sender in row.TopSenders.Where(sender => sender.Owner is not null && (double)sender.Failing / row.Messages > FailingSenderShare))
            {
                steps.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{sender.Owner} sent {sender.Failing:N0} messages as {domain.Name} that failed DMARC. If they send for you, add them to SPF or set up DKIM for them."));
            }
        }

        foreach (var domain in domains.Where(domain => !rows.TryGetValue(domain.Name, out var row) || row.Messages == 0))
        {
            steps.Add($"No DMARC reports arrived for {domain.Name}. Check its DMARC record's reporting address.");
        }

        return steps.Count == 0 ? [NothingToDo] : steps.Distinct(StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> CheckSteps(Domain domain)
    {
        var name = domain.Name;
        var reportingStep = $"Add the record that lets {name}'s DMARC reports reach your reporting mailbox.";
        switch (domain.DmarcCheckStatus)
        {
            case DmarcCheckStatus.MissingOwnRecord: yield return $"Publish a DMARC record for {name}."; break;
            case DmarcCheckStatus.Misconfigured: yield return $"Fix the DMARC record for {name}. Receivers may ignore it as it is."; break;
            case DmarcCheckStatus.MissingAuthorizationRecord: yield return reportingStep; break;
        }

        if (domain.DmarcAuthorizationCheckStatus == DmarcAuthorizationCheckStatus.Missing) yield return reportingStep;

        switch (domain.SpfCheckStatus)
        {
            case SpfCheckStatus.MissingRecord: yield return $"Publish an SPF record for {name}."; break;
            case SpfCheckStatus.MultipleRecords: yield return $"{name} has more than one SPF record. Merge them into one."; break;
            case SpfCheckStatus.Misconfigured: yield return $"Fix the SPF record for {name}. It has errors."; break;
            case SpfCheckStatus.TooManyLookups: yield return $"The SPF record for {name} needs more than 10 DNS lookups. Trim or flatten its includes."; break;
        }

        switch (domain.MxCheckStatus)
        {
            case MxCheckStatus.MissingRecord: yield return $"{name} has no MX record, so it can't receive mail. Publish one, or a null MX if it shouldn't."; break;
            case MxCheckStatus.UnresolvableTarget: yield return $"The mail server named in {name}'s MX record doesn't resolve. Fix the MX record."; break;
        }

        switch (domain.DkimCheckStatus)
        {
            case DkimCheckStatus.Missing: yield return $"A DKIM key for {name} is missing from DNS. Publish it again with the service that sends as {name}."; break;
            case DkimCheckStatus.Misconfigured: yield return $"A DKIM key for {name} is published incorrectly. Check it with the service that sends as {name}."; break;
        }

        switch (domain.TlsrptCheckStatus)
        {
            case TlsrptCheckStatus.MissingOwnRecord: yield return $"Publish a TLS reporting record for {name}, so delivery problems are reported."; break;
            case TlsrptCheckStatus.Misconfigured: yield return $"Fix the TLS reporting record for {name}."; break;
        }

        if (domain.MtaStsStatus == MtaStsStatus.Failed) yield return $"MTA-STS for {name} is failing. Check its policy host.";
    }
}
```

```csharp
// src/DotMarc/Reporting/ClientReports/ClientReportBuilder.cs
using DotMarc.Data;
using DotMarc.IpEnrichment;
using DotMarc.Portal;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Reporting.ClientReports;

/// <summary>Loads what a Group's report needs (its monitored domains, the period's and comparison period's reports,
/// alerts, sender owners and brand) and hands it to <see cref="ClientReportCalculator"/>.</summary>
public sealed class ClientReportBuilder(IDbContextFactory<DotMarcDbContext> dbFactory, PortalBrandLoader brandLoader, TimeProvider timeProvider)
{
    public async Task<ClientReport?> BuildAsync(int groupId, ReportPeriod period, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var group = await context.Groups.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == groupId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return null;
        }

        var domains = await context.Domains.AsNoTracking()
            .Where(domain => domain.IsMonitored && domain.Groups.Any(candidate => candidate.Id == groupId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var domainIds = domains.Select(domain => domain.Id).ToList();
        var domainNames = domains.Select(domain => domain.Name).ToList();

        var periodStartUtc = ReportPeriods.StartUtc(period, zone);
        var periodEndUtc = ReportPeriods.EndUtc(period, zone);
        var loadFromUtc = ReportPeriods.StartUtc(ReportPeriods.PreviousForComparison(period), zone);
        var reports = await context.Reports.AsNoTracking()
            .Where(report => domainIds.Contains(report.DomainId) && report.DateRangeBeginUtc >= loadFromUtc && report.DateRangeBeginUtc < periodEndUtc)
            .Include(report => report.Records)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var periodAlerts = await context.AlertEvents.AsNoTracking()
            .Where(alert => domainNames.Contains(alert.DomainName)
                && ((alert.CreatedUtc >= periodStartUtc && alert.CreatedUtc < periodEndUtc) || (alert.ResolvedUtc >= periodStartUtc && alert.ResolvedUtc < periodEndUtc)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var openAlerts = await context.AlertEvents.AsNoTracking()
            .Where(alert => domainNames.Contains(alert.DomainName) && !alert.IsResolved)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var ips = reports.Where(report => report.DateRangeBeginUtc >= periodStartUtc)
            .SelectMany(report => report.Records).Select(record => record.SourceIp).Distinct().ToList();
        var owners = await IpInfoService.GetCachedAsync(context, ips, cancellationToken).ConfigureAwait(false);

        var brand = await brandLoader.LoadAsync([groupId], cancellationToken).ConfigureAwait(false);
        byte[]? logo = null;
        if (brand.LogoImageId is { } logoId)
        {
            logo = await context.BrandingImages.AsNoTracking()
                .Where(image => image.Id == logoId && (image.ContentType == "image/png" || image.ContentType == "image/jpeg"))
                .Select(image => image.Bytes)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        return ClientReportCalculator.Build(new ClientReportInputs(
            brand, logo, group.Name, period, zone, domains, reports, periodAlerts, openAlerts, owners, timeProvider.GetUtcNow()));
    }
}
```

(Check `IpInfoService`'s namespace with `grep -n namespace src/DotMarc/IpEnrichment/IpInfoService.cs` and use it.) Register in `Program.cs`: `builder.Services.AddScoped<DotMarc.Reporting.ClientReports.ClientReportBuilder>();`

- [ ] **Step 4: Run the tests and commit**

Run the Step 2 filter (PASS), then `dotnet test test/DotMarc.Tests` (including the existing portal tests, which now go through `PortalWording`).

```bash
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Work out a Group's report: figures, trend, senders and next steps"
```

---

### Task 4: The PDF and the email body

**Files:**
- Create: `src/DotMarc/Reporting/ClientReports/Fonts/Roboto-Regular.ttf`, `src/DotMarc/Reporting/ClientReports/Fonts/Roboto-Bold.ttf`, `src/DotMarc/Reporting/ClientReports/Fonts/LICENSE.txt`, `src/DotMarc/Reporting/ClientReports/ReportFontResolver.cs`, `src/DotMarc/Reporting/ClientReports/ClientReportDocument.cs`, `src/DotMarc/Reporting/ClientReports/ClientReportEmail.cs`
- Modify: `src/DotMarc/DotMarc.csproj` (PDFsharp-MigraDoc, embedded fonts), `src/DotMarc/Components/Portal/BrandLogoField.razor` (PDF note)
- Test: `test/DotMarc.Tests/Reporting/ClientReports/ClientReportDocumentTests.cs`, `test/DotMarc.Tests/Reporting/ClientReports/ClientReportEmailTests.cs`, `test/DotMarc.Tests/Reporting/ClientReports/MigraDocText.cs`

**Interfaces:**
- Consumes: `ClientReport` and friends (Task 3); `EmailMessage`, `EmailAttachment` (Task 1).
- Produces:
  - `ReportFontResolver.EnsureInstalled()`
  - `ClientReportDocument.Build(ClientReport) : MigraDoc.DocumentObjectModel.Document`, `ClientReportDocument.Render(ClientReport) : byte[]`, `ClientReportDocument.FileName(ClientReport) : string`
  - `ClientReportEmail.Compose(ClientReport report, byte[] pdf, IReadOnlyList<string> to) : EmailMessage`, `ClientReportEmail.Subject(ClientReport) : string`

- [ ] **Step 1: Add the package and fonts**

```bash
dotnet add src/DotMarc/DotMarc.csproj package PDFsharp-MigraDoc
```

Download the static Roboto 2 fonts (Apache 2.0) from the `googlefonts/roboto-2` GitHub release `v2.138`, file `roboto-unhinted.zip`, and copy `Roboto-Regular.ttf` and `Roboto-Bold.ttf` into `src/DotMarc/Reporting/ClientReports/Fonts/`, with the Apache 2.0 licence text as `LICENSE.txt`. (If that release has moved, any static, non-variable Roboto Regular and Bold TTFs under Apache 2.0 will do; variable fonts are not supported by PDFsharp.) In `DotMarc.csproj`:

```xml
<ItemGroup>
  <EmbeddedResource Include="Reporting/ClientReports/Fonts/*.ttf" />
</ItemGroup>
```

- [ ] **Step 2: Write the failing tests**

```csharp
// test/DotMarc.Tests/Reporting/ClientReports/MigraDocText.cs
using System.Text;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;

namespace DotMarc.Tests.Reporting.ClientReports;

/// <summary>All the text in a MigraDoc document, for asserting on what a report says without parsing the PDF (whose
/// embedded font stores glyph ids, not letters).</summary>
internal static class MigraDocText
{
    public static string Of(Document document)
    {
        var text = new StringBuilder();
        foreach (Section section in document.Sections)
        {
            Append(section.Elements, text);
        }

        return text.ToString();
    }

    private static void Append(DocumentElements elements, StringBuilder text)
    {
        foreach (var element in elements)
        {
            switch (element)
            {
                case Paragraph paragraph:
                    AppendInline(paragraph.Elements, text);
                    text.AppendLine();
                    break;
                case Table table:
                    foreach (Row row in table.Rows)
                    {
                        foreach (Cell cell in row.Cells)
                        {
                            Append(cell.Elements, text);
                        }
                    }

                    break;
            }
        }
    }

    private static void AppendInline(ParagraphElements elements, StringBuilder text)
    {
        foreach (var element in elements)
        {
            switch (element)
            {
                case Text run: text.Append(run.Content); break;
                case FormattedText formatted: AppendInline(formatted.Elements, text); break;
                case Character character when character.SymbolName == SymbolName.Blank: text.Append(' '); break;
            }
        }
    }
}
```

(MigraDoc 6 type names: `Document`, `Section`, `DocumentElements`, `Paragraph`, `ParagraphElements`, `Text`, `FormattedText`, `Character`, `Table`, `Row`, `Cell`. Adjust to the installed version if a name differs.)

```csharp
// test/DotMarc.Tests/Reporting/ClientReports/ClientReportDocumentTests.cs
using DotMarc.Portal;
using DotMarc.Reporting.ClientReports;
using MudBlazor;
using PdfSharp.Pdf.IO;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

public sealed class ClientReportDocumentTests
{
    internal static ClientReport SampleReport(byte[]? logo = null, IReadOnlyList<ClientReportDomain>? domains = null) => new(
        new ResolvedBrand("Nova MSP", "Aurora Retail Ltd", "#0B5FFF", "#FF6B00", null, null, "help@nova-msp.example", null, null, null),
        logo, "Aurora Retail", new ReportPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), ReportPeriodKind.Month), "Europe/London",
        "1 of 2 domains are fully protected. 1 needs attention.",
        domains ??
        [
            new("aurora-retail.example", new PortalDomainStatus(PortalHealth.NeedsAttention, ["SPF: Missing"]), 1200, 0.95, 0.9,
                [0.9, null, 1.0], "Mail that fails DMARC is rejected.", [new HealthRow("SPF", "Missing", Color.Error)],
                [new ClientReportSender("203.0.113.10", "Google LLC", 1000, 990, 10, 0.83)], new ReceiverActions(1100, 60, 40, 5),
                [new ClientReportAlert("SPF record broken", new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero), null)]),
            new("shop.aurora-retail.example", new PortalDomainStatus(PortalHealth.Protected, []), 300, 1.0, null,
                [1.0, 1.0, 1.0], "Mail that fails DMARC is rejected.", [], [], new ReceiverActions(300, 0, 0, 0), []),
        ],
        ["Publish an SPF record for aurora-retail.example."], new DateTimeOffset(2026, 4, 1, 5, 0, 0, TimeSpan.Zero));

    [Fact]
    public void TheDocument_SaysWhatTheReportSays()
    {
        var text = MigraDocText.Of(ClientReportDocument.Build(SampleReport()));

        Assert.Contains("Aurora Retail Ltd", text);
        Assert.Contains("March 2026", text);
        Assert.Contains("1 March 2026 to 31 March 2026", text);
        Assert.Contains("1 of 2 domains are fully protected. 1 needs attention.", text);
        Assert.Contains("aurora-retail.example", text);
        Assert.Contains("shop.aurora-retail.example", text);
        Assert.Contains("+5.0 pts", text);
        Assert.Contains("Google LLC", text);
        Assert.Contains("Publish an SPF record for aurora-retail.example.", text);
        Assert.Contains("SPF record broken", text);
        Assert.Contains("help@nova-msp.example", text);
    }

    [Fact]
    public void AGroupWithNoDomains_StillRenders_AndSaysSo()
    {
        var report = SampleReport(domains: []) with { Verdict = "There are no domains in this report.", NextSteps = [ClientReportNextSteps.NothingToDo] };

        var pdf = ClientReportDocument.Render(report);

        Assert.Contains("There are no domains in this report.", MigraDocText.Of(ClientReportDocument.Build(report)));
        Assert.Equal(1, PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import).PageCount);
    }

    [Fact]
    public void ThePdf_IsAPdf_WithItsFontEmbedded()
    {
        var pdf = ClientReportDocument.Render(SampleReport());

        Assert.Equal("%PDF-"u8.ToArray(), pdf[..5]);
        Assert.Contains("Roboto", System.Text.Encoding.Latin1.GetString(pdf));
    }

    [Fact]
    public void APngLogo_IsDrawn_AndWithoutOneTheProductNameIs()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        var withLogo = ClientReportDocument.Build(SampleReport(logo: png));
        var withoutLogo = MigraDocText.Of(ClientReportDocument.Build(SampleReport()));

        Assert.Contains(withLogo.Sections[0].Elements.OfType<MigraDoc.DocumentObjectModel.Shapes.Image>(), _ => true);
        Assert.Contains("Nova MSP", withoutLogo);
        ClientReportDocument.Render(SampleReport(logo: png)); // renders without throwing
    }

    [Fact]
    public void TheFileName_IsSafe()
    {
        var report = SampleReport() with { Brand = SampleReport().Brand with { Heading = "Aurora/Retail: \"Ltd\"" } };

        Assert.Equal("Aurora Retail Ltd email security report March 2026.pdf", ClientReportDocument.FileName(report));
    }
}
```

(If the logo is placed inside a table cell or header rather than the section body, assert on wherever the builder puts it.)

```csharp
// test/DotMarc.Tests/Reporting/ClientReports/ClientReportEmailTests.cs
using DotMarc.Reporting.ClientReports;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

public sealed class ClientReportEmailTests
{
    [Fact]
    public void TheEmail_HasTheSubjectSummaryAndAttachment()
    {
        var report = ClientReportDocumentTests.SampleReport();

        var message = ClientReportEmail.Compose(report, [0x25, 0x50], ["it@aurora-retail.example"]);

        Assert.Equal("Aurora Retail Ltd email security report: March 2026", message.Subject);
        Assert.Contains("1 of 2 domains are fully protected.", message.HtmlBody);
        Assert.Contains("aurora-retail.example", message.HtmlBody);
        Assert.Contains("The full report is attached.", message.TextBody);
        Assert.Equal(("Aurora Retail Ltd email security report March 2026.pdf", "application/pdf"),
            (message.Attachments.Single().FileName, message.Attachments.Single().ContentType));
        Assert.DoesNotContain("<img", message.HtmlBody);
    }

    [Fact]
    public void NamesFromData_AreText_NotMarkup()
    {
        var report = ClientReportDocumentTests.SampleReport() with
        {
            Brand = ClientReportDocumentTests.SampleReport().Brand with { Heading = "<script>alert(1)</script>" },
        };

        var message = ClientReportEmail.Compose(report, [0x25], ["it@aurora-retail.example"]);

        Assert.DoesNotContain("<script>", message.HtmlBody);
        Assert.Contains("&lt;script&gt;", message.HtmlBody);
    }
}
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ClientReportDocumentTests|FullyQualifiedName~ClientReportEmailTests"`
Expected: build FAIL.

- [ ] **Step 4: Implement**

```csharp
// src/DotMarc/Reporting/ClientReports/ReportFontResolver.cs
using PdfSharp.Fonts;

namespace DotMarc.Reporting.ClientReports;

/// <summary>Serves the bundled Roboto to PDFsharp, so reports look the same on any server, including a container with
/// no system fonts. Every family name resolves to Roboto; italic is simulated.</summary>
public sealed class ReportFontResolver : IFontResolver
{
    public const string FamilyName = "Roboto";
    private const string Regular = "Roboto-Regular";
    private const string Bold = "Roboto-Bold";
    private static readonly object InstallLock = new();

    public static void EnsureInstalled()
    {
        lock (InstallLock)
        {
            GlobalFontSettings.FontResolver ??= new ReportFontResolver();
        }
    }

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(isBold ? Bold : Regular, mustSimulateBold: false, mustSimulateItalic: isItalic);

    public byte[] GetFont(string faceName)
    {
        using var stream = typeof(ReportFontResolver).Assembly.GetManifestResourceStream($"DotMarc.Reporting.ClientReports.Fonts.{faceName}.ttf")
            ?? throw new InvalidOperationException($"The bundled font {faceName} is missing from the build.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
```

(Check the manifest resource name with `typeof(ReportFontResolver).Assembly.GetManifestResourceNames()` if the font isn't found; it follows the default namespace plus folder path.)

```csharp
// src/DotMarc/Reporting/ClientReports/ClientReportDocument.cs
using System.Globalization;
using System.Text.RegularExpressions;
using DotMarc.Portal;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Shapes.Charts;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

namespace DotMarc.Reporting.ClientReports;

/// <summary>Lays a report out as a MigraDoc document (A4, the brand's colours, the bundled Roboto) and renders it to
/// PDF. Building and rendering are separate so tests can read the document's text.</summary>
public static partial class ClientReportDocument
{
    private const int ChartedDomainLimit = 6;
    private static readonly string[] SeriesColours = ["#0B5FFF", "#E3594F", "#2E7D32", "#F9A825", "#6A1B9A", "#00838F"];

    public static byte[] Render(ClientReport report)
    {
        ReportFontResolver.EnsureInstalled();
        var renderer = new PdfDocumentRenderer { Document = Build(report) };
        renderer.RenderDocument();
        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    public static string FileName(ClientReport report)
    {
        var safe = SpacesPattern().Replace(UnsafePattern().Replace($"{report.Brand.Heading} email security report {report.Period.Label}", " "), " ").Trim();
        return $"{safe}.pdf";
    }

    public static Document Build(ClientReport report)
    {
        var primary = Hex(report.Brand.PrimaryColour);
        var document = new Document();
        document.Info.Title = $"{report.Brand.Heading} email security report: {report.Period.Label}";
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = ReportFontResolver.FamilyName;
        normal.Font.Size = 10;
        document.Styles[StyleNames.Heading1]!.Font.Size = 20;
        document.Styles[StyleNames.Heading1]!.Font.Bold = true;
        document.Styles[StyleNames.Heading1]!.Font.Color = primary;
        document.Styles[StyleNames.Heading2]!.Font.Size = 14;
        document.Styles[StyleNames.Heading2]!.Font.Bold = true;
        document.Styles[StyleNames.Heading2]!.ParagraphFormat.SpaceBefore = Unit.FromPoint(14);
        document.Styles[StyleNames.Heading2]!.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);

        var section = document.AddSection();
        section.PageSetup = document.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(2);
        section.Footers.Primary.AddParagraph(Footer(report)).Format.Font.Size = 8;

        AddCover(section, report, primary);
        if (report.Domains.Count > 0)
        {
            AddSummary(section, report);
            AddTrend(section, report);
            foreach (var domain in report.Domains)
            {
                AddDomain(section, domain);
            }
        }

        section.AddParagraph("What to do next", StyleNames.Heading2);
        foreach (var step in report.NextSteps)
        {
            var paragraph = section.AddParagraph();
            paragraph.Format.LeftIndent = Unit.FromCentimeter(0.4);
            paragraph.AddText("• " + step);
        }

        return document;
    }

    private static string Footer(ClientReport report)
    {
        var contact = new[] { report.Brand.SupportEmail, report.Brand.SupportUrl, report.Brand.SupportPhone }.OfType<string>();
        var parts = new[] { report.Brand.ProductName }.Concat(contact).Append($"Times in {report.TimeZoneId}");
        return string.Join("  |  ", parts);
    }

    private static void AddCover(Section section, ClientReport report, Color primary)
    {
        if (report.Logo is { } logo)
        {
            var image = section.AddImage("base64:" + Convert.ToBase64String(logo));
            image.Height = Unit.FromCentimeter(1.5);
            image.LockAspectRatio = true;
        }
        else
        {
            var name = section.AddParagraph(report.Brand.ProductName);
            name.Format.Font.Size = 14;
            name.Format.Font.Bold = true;
            name.Format.Font.Color = primary;
        }

        section.AddParagraph(report.Brand.Heading, StyleNames.Heading1).Format.SpaceBefore = Unit.FromPoint(12);
        section.AddParagraph($"Email security report: {report.Period.Label}").Format.Font.Size = 14;
        section.AddParagraph($"{ReportPeriod.Format(report.Period.Start)} to {ReportPeriod.Format(report.Period.End)}").Format.Font.Color = Colors.Gray;
        var verdict = section.AddParagraph(report.Verdict);
        verdict.Format.SpaceBefore = Unit.FromPoint(12);
        verdict.Format.Font.Size = 12;
        verdict.Format.Font.Bold = true;
    }

    private static void AddSummary(Section section, ClientReport report)
    {
        section.AddParagraph("Summary", StyleNames.Heading2);
        var table = NewTable(section, ("Domain", 6.0), ("Status", 3.2), ("Messages", 2.4), ("Pass rate", 2.2), ("Change", 2.2));
        foreach (var domain in report.Domains)
        {
            AddRow(table, domain.Name, StatusText(domain.Status.Health), Count(domain.Messages), Percent(domain.PassRate), domain.ChangeText);
            table.Rows[^1].Cells[1].Format.Font.Color = StatusColour(domain.Status.Health);
        }
    }

    private static void AddTrend(Section section, ClientReport report)
    {
        section.AddParagraph("Pass rate trend", StyleNames.Heading2);
        var charted = report.Domains.OrderByDescending(domain => domain.Messages).Take(ChartedDomainLimit).ToList();
        var chart = new Chart(ChartType.Line)
        {
            Width = Unit.FromCentimeter(16),
            Height = Unit.FromCentimeter(6),
            DisplayBlanksAs = BlankType.NotPlotted,
        };
        chart.YAxis.MinimumScale = 0;
        chart.YAxis.MaximumScale = 100;
        chart.YAxis.MajorTickMark = TickMarkType.Outside;
        chart.YAxis.HasMajorGridlines = true;
        chart.XAxis.MajorTickMark = TickMarkType.None;
        chart.TopArea.AddLegend();
        var points = charted.Count == 0 ? 0 : charted[0].Trend.Count;
        var labels = chart.XValues.AddXSeries();
        for (var point = 0; point < points; point++)
        {
            labels.Add(point % Math.Max(1, points / 6) == 0 ? (point + 1).ToString(CultureInfo.InvariantCulture) : "");
        }

        for (var index = 0; index < charted.Count; index++)
        {
            var series = chart.SeriesCollection.AddSeries();
            series.Name = charted[index].Name;
            series.LineFormat.Color = Hex(SeriesColours[index]);
            series.MarkerStyle = MarkerStyle.None;
            foreach (var rate in charted[index].Trend)
            {
                if (rate is { } value)
                {
                    series.Add(value * 100);
                }
                else
                {
                    series.AddBlank();
                }
            }
        }

        section.Add(chart);
        var others = report.Domains.Except(charted).Select(domain => domain.Name).ToList();
        if (others.Count > 0)
        {
            section.AddParagraph($"Not charted: {string.Join(", ", others)}").Format.Font.Size = 8;
        }
    }

    private static void AddDomain(Section section, ClientReportDomain domain)
    {
        section.AddParagraph(domain.Name, StyleNames.Heading2);
        var status = section.AddParagraph(StatusText(domain.Status.Health));
        status.Format.Font.Bold = true;
        status.Format.Font.Color = StatusColour(domain.Status.Health);
        foreach (var reason in domain.Status.Reasons)
        {
            section.AddParagraph(reason);
        }

        section.AddParagraph(domain.PolicySentence).Format.SpaceBefore = Unit.FromPoint(4);

        if (domain.Health.Count > 0)
        {
            var health = NewTable(section, ("Check", 5.0), ("Result", 11.0));
            foreach (var row in domain.Health)
            {
                AddRow(health, row.Name, row.Label);
                health.Rows[^1].Cells[1].Format.Font.Color = HealthColour(row.Colour);
            }
        }

        section.AddParagraph("Who sent as this domain").Format.Font.Bold = true;
        if (domain.TopSenders.Count == 0)
        {
            section.AddParagraph("No mail was reported in this period.");
        }
        else
        {
            var senders = NewTable(section, ("Sender", 5.6), ("Messages", 2.4), ("Passed", 2.4), ("Failed", 2.4), ("Share", 3.2));
            foreach (var sender in domain.TopSenders)
            {
                AddRow(senders, sender.Owner is null ? sender.Ip : $"{sender.Owner} ({sender.Ip})", Count(sender.Messages), Count(sender.Passing), Count(sender.Failing), Percent(sender.Share));
            }
        }

        var receivers = domain.Receivers;
        var total = receivers.Delivered + receivers.Quarantined + receivers.Rejected;
        section.AddParagraph("What receivers did").Format.Font.Bold = true;
        section.AddParagraph(total == 0
            ? "No mail was reported in this period."
            : $"Delivered {Count(receivers.Delivered)} ({Percent((double)receivers.Delivered / total)}), sent to spam {Count(receivers.Quarantined)} ({Percent((double)receivers.Quarantined / total)}), rejected {Count(receivers.Rejected)} ({Percent((double)receivers.Rejected / total)}).");
        if (receivers.FailingDelivered > 0)
        {
            section.AddParagraph($"{Count(receivers.FailingDelivered)} messages failed DMARC but were delivered anyway, because the policy doesn't block them yet.");
        }

        if (domain.Alerts.Count > 0)
        {
            section.AddParagraph("Alerts").Format.Font.Bold = true;
            foreach (var alert in domain.Alerts)
            {
                var raised = ReportPeriod.Format(DateOnly.FromDateTime(alert.CreatedUtc.UtcDateTime));
                var resolved = alert.ResolvedUtc is { } at ? $", resolved {ReportPeriod.Format(DateOnly.FromDateTime(at.UtcDateTime))}" : ", still open";
                section.AddParagraph($"{alert.Title}: raised {raised}{resolved}.");
            }
        }
    }

    private static Table NewTable(Section section, params (string Header, double WidthCm)[] columns)
    {
        var table = section.AddTable();
        table.Borders.Bottom.Width = 0.5;
        table.Borders.Color = Colors.LightGray;
        table.Format.SpaceBefore = Unit.FromPoint(2);
        table.Format.SpaceAfter = Unit.FromPoint(2);
        foreach (var column in columns)
        {
            table.AddColumn(Unit.FromCentimeter(column.WidthCm));
        }

        var header = table.AddRow();
        header.HeadingFormat = true;
        header.Format.Font.Bold = true;
        for (var index = 0; index < columns.Length; index++)
        {
            header.Cells[index].AddParagraph(columns[index].Header);
        }

        return table;
    }

    private static void AddRow(Table table, params string[] values)
    {
        var row = table.AddRow();
        for (var index = 0; index < values.Length; index++)
        {
            row.Cells[index].AddParagraph(values[index]);
        }
    }

    private static string StatusText(PortalHealth health) => health switch
    {
        PortalHealth.Protected => "Protected",
        PortalHealth.MonitoringOnly => "Monitoring only",
        PortalHealth.NoReportsYet => "No reports yet",
        _ => "Needs attention",
    };

    private static Color StatusColour(PortalHealth health) => health switch
    {
        PortalHealth.Protected => Hex("#2E7D32"),
        PortalHealth.NeedsAttention => Hex("#C62828"),
        _ => Hex("#EF6C00"),
    };

    private static Color HealthColour(MudBlazor.Color colour) => colour switch
    {
        MudBlazor.Color.Success => Hex("#2E7D32"),
        MudBlazor.Color.Error => Hex("#C62828"),
        MudBlazor.Color.Warning => Hex("#EF6C00"),
        _ => Colors.Gray,
    };

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Percent(double? value) => value is { } rate ? rate.ToString("P1", CultureInfo.InvariantCulture) : "No mail";

    private static Color Hex(string hex) => new(
        byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    [GeneratedRegex("[^A-Za-z0-9 ._-]")]
    private static partial Regex UnsafePattern();

    [GeneratedRegex(" {2,}")]
    private static partial Regex SpacesPattern();
}
```

(MigraDoc API notes for the installed 6.x version: `Series.AddBlank()` and `Chart.DisplayBlanksAs = BlankType.NotPlotted` plot gaps; `section.AddImage("base64:...")` takes an image from bytes; `PdfDocumentRenderer.PdfDocument.Save(Stream, bool)` saves. If one of these differs, keep the behaviour: gaps where there's no data, the logo drawn from its bytes, no temporary files.)

```csharp
// src/DotMarc/Reporting/ClientReports/ClientReportEmail.cs
using System.Net;
using System.Text;
using DotMarc.Email;

namespace DotMarc.Reporting.ClientReports;

/// <summary>The short branded email a report goes out in: the verdict and summary, with the PDF attached. No remote
/// images, so nothing is blocked or tracked. Everything from data is HTML-encoded.</summary>
public static class ClientReportEmail
{
    public static string Subject(ClientReport report) => $"{report.Brand.Heading} email security report: {report.Period.Label}";

    public static EmailMessage Compose(ClientReport report, byte[] pdf, IReadOnlyList<string> to)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);

        var html = new StringBuilder();
        html.Append("<div style=\"font-family:Arial,Helvetica,sans-serif;font-size:14px;color:#222\">");
        html.Append($"<div style=\"border-top:6px solid {E(report.Brand.PrimaryColour)};padding-top:12px\">");
        html.Append($"<h1 style=\"font-size:20px;margin:0 0 4px\">{E(report.Brand.Heading)}</h1>");
        html.Append($"<p style=\"margin:0 0 12px;color:#555\">Email security report: {E(report.Period.Label)}</p>");
        html.Append($"<p style=\"font-weight:bold\">{E(report.Verdict)}</p>");
        if (report.Domains.Count > 0)
        {
            html.Append("<table cellpadding=\"6\" style=\"border-collapse:collapse\"><tr style=\"text-align:left\"><th>Domain</th><th>Messages</th><th>Pass rate</th><th>Change</th></tr>");
            foreach (var domain in report.Domains)
            {
                html.Append($"<tr><td>{E(domain.Name)}</td><td>{domain.Messages:N0}</td><td>{E(domain.PassRate?.ToString("P1", System.Globalization.CultureInfo.InvariantCulture) ?? "No mail")}</td><td>{E(domain.ChangeText)}</td></tr>");
            }

            html.Append("</table>");
        }

        html.Append("<p>The full report is attached.</p>");
        var contact = new[] { report.Brand.SupportEmail, report.Brand.SupportUrl, report.Brand.SupportPhone }.OfType<string>().ToList();
        if (contact.Count > 0)
        {
            html.Append($"<p style=\"color:#555\">Questions? {E(string.Join("  |  ", contact))}</p>");
        }

        html.Append($"<p style=\"color:#888;font-size:12px\">{E(report.Brand.ProductName)}</p></div></div>");

        var text = new StringBuilder()
            .AppendLine($"{report.Brand.Heading}: email security report, {report.Period.Label}")
            .AppendLine()
            .AppendLine(report.Verdict)
            .AppendLine();
        foreach (var domain in report.Domains)
        {
            text.AppendLine($"{domain.Name}: {domain.Messages:N0} messages, pass rate {domain.PassRate?.ToString("P1", System.Globalization.CultureInfo.InvariantCulture) ?? "no mail"} {domain.ChangeText}".TrimEnd());
        }

        text.AppendLine().AppendLine("The full report is attached.");
        if (contact.Count > 0)
        {
            text.AppendLine($"Questions? {string.Join("  |  ", contact)}");
        }

        return new EmailMessage(to, Subject(report), html.ToString(), text.ToString(),
            [new EmailAttachment(ClientReportDocument.FileName(report), "application/pdf", pdf)]);
    }
}
```

In `BrandLogoField.razor`, under the field, add a caption shown for every logo field: "Reports (PDF) use PNG or JPEG logos. With only an SVG, reports show your product name instead."

- [ ] **Step 5: Run the tests and commit**

Run the Step 3 filter (PASS), then `dotnet test test/DotMarc.Tests`.

```bash
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Render client reports as branded PDFs with a short email"
```

---

### Task 5: Sending: scheduler, alert, send now and download

**Files:**
- Create: `src/DotMarc/Reporting/ClientReports/ClientReportDispatcher.cs`, `src/DotMarc/Reporting/ClientReports/ClientReportScheduler.cs`, `src/DotMarc/Reporting/ClientReports/ClientReportRunner.cs`
- Modify: `src/DotMarc/Notifications/AlertTypes.cs`, `src/DotMarc/Notifications/AlertingService.cs`, `test/DotMarc.Tests/Internal/FakeAlertingService.cs`, `test/DotMarc.Tests/Ingestion/PollingServiceDiActivationTests.cs` (`NoOpAlertingService`), `src/DotMarc/Program.cs`
- Test: `test/DotMarc.Tests/Reporting/ClientReports/ClientReportDispatcherTests.cs`, `test/DotMarc.Tests/Reporting/ClientReports/ClientReportRunnerTests.cs`, `test/DotMarc.Tests/Reporting/ClientReports/ClientReportDownloadTests.cs`, `test/DotMarc.Tests/Notifications/ClientReportAlertTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1 to 4.
- Produces:
  - `AlertTypes.ClientReportFailed = "ClientReportFailed"` ("Client report failed", `CreatesTicketByDefault: false`)
  - `IAlertingService.RaiseClientReportFailedAsync(int groupId, string groupName, string periodLabel, string error, CancellationToken)`, `IAlertingService.ResolveClientReportFailedAsync(int groupId, CancellationToken)`, `AlertingService.ClientReportAlertSubject(int groupId, string groupName) : string`
  - `ClientReportDispatcher(IDbContextFactory<DotMarcDbContext>, ClientReportBuilder, IEmailSenderFactory, IAlertingService, TimeProvider, ILogger<ClientReportDispatcher>)`, `Task RunOnceAsync(CancellationToken)`, `RetryInterval = 1h`, `GiveUpAfter = 24h`
  - `ClientReportScheduler` hosted service (every 15 minutes)
  - `ClientReportRunner(IDbContextFactory<DotMarcDbContext>, ClientReportBuilder, IEmailSenderFactory, IAlertingService, TimeProvider)` scoped: `Task<ManualSendResult> SendNowAsync(AuditActor actor, int groupId, ReportPeriod period, IReadOnlyList<string> recipients, CancellationToken)`, `Task<(byte[] Pdf, string FileName)?> RenderAsync(int groupId, ReportPeriod period, CancellationToken)`, `Task<TimeZoneInfo> ZoneAsync(CancellationToken)`; `record ManualSendResult(bool Sent, string Message)`
  - endpoint `GET /reports/groups/{groupId:int}/pdf?start=yyyy-MM-dd&end=yyyy-MM-dd` (policy `ReportsManage`)

- [ ] **Step 1: Write the failing tests**

```csharp
// test/DotMarc.Tests/Reporting/ClientReports/ClientReportDispatcherTests.cs
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Email;
using DotMarc.Portal;
using DotMarc.Reporting.ClientReports;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

[Collection("Postgres")]
public sealed class ClientReportDispatcherTests : IAsyncLifetime
{
    private sealed class RecordingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public Exception? FailWith { get; set; }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            if (FailWith is not null)
            {
                throw FailWith;
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedSenderFactory(IEmailSender? sender) : IEmailSenderFactory
    {
        public Task<IEmailSender?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(sender);
    }

    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;
    private readonly RecordingSender _sender = new();
    private readonly RecordingAlerts _alerts = new();

    // 1 April 2026 05:00 UTC: March is complete but not yet due (due 06:00 UTC).
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 4, 1, 5, 0, 0, TimeSpan.Zero));

    public ClientReportDispatcherTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    private ClientReportDispatcher Dispatcher(IEmailSender? sender = null, bool emailOff = false)
    {
        var factory = new FakeDbContextFactory(_connectionString);
        return new ClientReportDispatcher(factory, new ClientReportBuilder(factory, new PortalBrandLoader(factory), _clock),
            new FixedSenderFactory(emailOff ? null : sender ?? _sender), _alerts, _clock, NullLogger<ClientReportDispatcher>.Instance);
    }

    /// <summary>A Group with one monitored domain and a monthly schedule started in February.</summary>
    private async Task<int> AddScheduledGroupAsync(string name = "Aurora Retail", ReportFrequency frequency = ReportFrequency.Monthly, bool withDomain = true)
    {
        await using var context = CreateContext();
        var group = new Group { Name = name };
        context.Groups.Add(group);
        if (withDomain)
        {
            context.Domains.Add(new Domain { Name = $"{name.ToLowerInvariant().Replace(' ', '-')}.example", IsMonitored = true, Groups = [group] });
        }

        await context.SaveChangesAsync();
        context.GroupReportSchedules.Add(new GroupReportSchedule
        {
            GroupId = group.Id, Frequency = frequency, Recipients = ["it@aurora-retail.example"], StartedUtc = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await context.SaveChangesAsync();
        return group.Id;
    }

    private async Task<ClientReportDelivery> DeliveryAsync(int groupId)
    {
        await using var context = CreateContext();
        return await context.ClientReportDeliveries.SingleAsync(delivery => delivery.GroupId == groupId);
    }

    [Fact]
    public async Task NothingIsSent_BeforeTheSendHour()
    {
        await AddScheduledGroupAsync();

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task ADuePeriod_IsSentOnce()
    {
        var groupId = await AddScheduledGroupAsync();
        _clock.Advance(TimeSpan.FromHours(1));

        await Dispatcher().RunOnceAsync(CancellationToken.None);
        await Dispatcher().RunOnceAsync(CancellationToken.None);

        var sent = Assert.Single(_sender.Sent);
        Assert.Equal("Aurora Retail email security report: March 2026", sent.Subject);
        var delivery = await DeliveryAsync(groupId);
        Assert.Equal((ClientReportDeliveryStatus.Sent, new DateOnly(2026, 3, 1), ClientReportDeliveryKind.Scheduled), (delivery.Status, delivery.PeriodStart, delivery.Kind));
    }

    [Fact]
    public async Task ASchedule_TurnedOnMidPeriod_WaitsForTheNextPeriod()
    {
        var groupId = await AddScheduledGroupAsync();
        await using (var context = CreateContext())
        {
            var schedule = await context.GroupReportSchedules.SingleAsync();
            schedule.StartedUtc = new DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero);
            await context.SaveChangesAsync();
        }

        _clock.Advance(TimeSpan.FromDays(15)); // 16 April: March is due but fell due before the schedule started

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Empty(_sender.Sent);
        await using var verify = CreateContext();
        Assert.False(await verify.ClientReportDeliveries.AnyAsync(delivery => delivery.GroupId == groupId));
    }

    [Fact]
    public async Task AFailure_IsRetriedHourly_ThenGivenUpAfter24Hours_WithAnAlert()
    {
        var groupId = await AddScheduledGroupAsync();
        var failing = new RecordingSender { FailWith = new EmailSendException("535 Authentication failed") };
        _clock.Advance(TimeSpan.FromHours(1));

        await Dispatcher(failing).RunOnceAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(30));
        await Dispatcher(failing).RunOnceAsync(CancellationToken.None); // too soon to retry
        Assert.Equal((1, ClientReportDeliveryStatus.Pending), ((await DeliveryAsync(groupId)).Attempts, (await DeliveryAsync(groupId)).Status));

        for (var hour = 0; hour < 24; hour++)
        {
            _clock.Advance(TimeSpan.FromHours(1));
            await Dispatcher(failing).RunOnceAsync(CancellationToken.None);
        }

        var delivery = await DeliveryAsync(groupId);
        Assert.Equal((ClientReportDeliveryStatus.Failed, "535 Authentication failed"), (delivery.Status, delivery.Error));
        Assert.Equal((groupId, "March 2026"), (_alerts.Raised.Single().GroupId, _alerts.Raised.Single().PeriodLabel));
    }

    [Fact]
    public async Task ALaterSuccess_ResolvesTheAlert()
    {
        var groupId = await AddScheduledGroupAsync();
        _clock.Advance(TimeSpan.FromHours(1));

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Contains(groupId, _alerts.Resolved);
    }

    [Fact]
    public async Task ASuccessAfterARename_StillResolvesTheAlert()
    {
        var groupId = await AddScheduledGroupAsync();
        await using (var context = CreateContext())
        {
            (await context.Groups.SingleAsync(group => group.Id == groupId)).Name = "Aurora Retail Group";
            await context.SaveChangesAsync();
        }

        _clock.Advance(TimeSpan.FromHours(1));
        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Contains(groupId, _alerts.Resolved); // resolved by Group id, not name
    }

    [Fact]
    public async Task OneFailingGroup_DoesntStopTheOthers()
    {
        await AddScheduledGroupAsync("Aurora Retail");
        await AddScheduledGroupAsync("Brightline Legal");
        var sometimesFailing = new FailFirstSender();
        _clock.Advance(TimeSpan.FromHours(1));

        await Dispatcher(sometimesFailing).RunOnceAsync(CancellationToken.None);

        Assert.Single(sometimesFailing.Sent);
    }

    [Fact]
    public async Task EmailOff_SkipsThePeriod_WithoutAnAlert()
    {
        var groupId = await AddScheduledGroupAsync();
        _clock.Advance(TimeSpan.FromHours(1));

        await Dispatcher(emailOff: true).RunOnceAsync(CancellationToken.None);

        var delivery = await DeliveryAsync(groupId);
        Assert.Equal((ClientReportDeliveryStatus.Skipped, "Email is off"), (delivery.Status, delivery.Error));
        Assert.Empty(_alerts.Raised);
    }

    [Fact]
    public async Task AGroupWithNoDomains_IsSkipped()
    {
        var groupId = await AddScheduledGroupAsync(withDomain: false);
        _clock.Advance(TimeSpan.FromHours(1));

        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Equal((ClientReportDeliveryStatus.Skipped, "No domains"), ((await DeliveryAsync(groupId)).Status, (await DeliveryAsync(groupId)).Error));
    }

    [Fact]
    public async Task AManualSend_DoesntBlockTheScheduledOne()
    {
        var groupId = await AddScheduledGroupAsync();
        await using (var context = CreateContext())
        {
            context.ClientReportDeliveries.Add(new ClientReportDelivery
            {
                GroupId = groupId, PeriodStart = new DateOnly(2026, 3, 1), PeriodEnd = new DateOnly(2026, 3, 31), Kind = ClientReportDeliveryKind.Manual,
                Recipients = ["it@aurora-retail.example"], Status = ClientReportDeliveryStatus.Sent, SentUtc = new DateTimeOffset(2026, 3, 31, 12, 0, 0, TimeSpan.Zero),
            });
            await context.SaveChangesAsync();
        }

        _clock.Advance(TimeSpan.FromHours(1));
        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task ChangingTheTimeZone_DoesntResendAPeriod()
    {
        await AddScheduledGroupAsync();
        _clock.Advance(TimeSpan.FromHours(1));
        await Dispatcher().RunOnceAsync(CancellationToken.None);
        await using (var context = CreateContext())
        {
            var settings = await context.ReportSettings.SingleAsync();
            settings.TimeZoneId = "Australia/Sydney";
            await context.SaveChangesAsync();
        }

        _clock.Advance(TimeSpan.FromHours(2));
        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task ARetry_GoesToTheCurrentRecipients()
    {
        await AddScheduledGroupAsync();
        var failing = new RecordingSender { FailWith = new EmailSendException("Mailbox unavailable") };
        _clock.Advance(TimeSpan.FromHours(1));
        await Dispatcher(failing).RunOnceAsync(CancellationToken.None);
        await using (var context = CreateContext())
        {
            (await context.GroupReportSchedules.SingleAsync()).Recipients = ["finance@aurora-retail.example"];
            await context.SaveChangesAsync();
        }

        _clock.Advance(TimeSpan.FromHours(1));
        await Dispatcher().RunOnceAsync(CancellationToken.None);

        Assert.Equal(["finance@aurora-retail.example"], _sender.Sent.Single().To);
    }

    private sealed class FailFirstSender : IEmailSender
    {
        private int _calls;
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            if (_calls++ == 0)
            {
                throw new EmailSendException("First one fails");
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
```

Add a recording fake the dispatcher and runner tests share:

```csharp
// test/DotMarc.Tests/Internal/RecordingAlerts.cs
using DotMarc.Notifications;
using DotMarc.Reporting;

namespace DotMarc.Tests.Internal;

/// <summary>Records client report alerts; the domain alert methods do nothing.</summary>
internal sealed class RecordingAlerts : IAlertingService
{
    public List<(int GroupId, string GroupName, string PeriodLabel, string Error)> Raised { get; } = [];
    public List<int> Resolved { get; } = [];

    public Task CheckPinnedDomainsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ResolveDomainAlertAsync(string domainName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task HandleTlsrptReportAsync(string domainName, long failedSessionCount, IReadOnlyList<string> failureTypes, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, ReasonBreakdown reasonBreakdown, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RaiseClientReportFailedAsync(int groupId, string groupName, string periodLabel, string error, CancellationToken cancellationToken = default)
    {
        Raised.Add((groupId, groupName, periodLabel, error));
        return Task.CompletedTask;
    }

    public Task ResolveClientReportFailedAsync(int groupId, CancellationToken cancellationToken = default)
    {
        Resolved.Add(groupId);
        return Task.CompletedTask;
    }
}
```

```csharp
// test/DotMarc.Tests/Notifications/ClientReportAlertTests.cs
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class ClientReportAlertTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ClientReportAlertTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var settings = await context.NotificationSettings.SingleAsync();
        settings.Enabled = true;
        await context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    [Fact]
    public async Task AFailedReport_RaisesAnAlert_ThatResolvesByGroupIdEvenAfterARename()
    {
        var alerting = new AlertingService(new FakeDbContextFactory(_connectionString), new FakeAlertWebhookClient(), PsaTestSupport.NoOpTicketService(), NullLogger<AlertingService>.Instance);

        await alerting.RaiseClientReportFailedAsync(7, "Aurora Retail", "March 2026", "535 Authentication failed");
        await alerting.ResolveClientReportFailedAsync(7);

        await using var verify = CreateContext();
        var alert = await verify.AlertEvents.SingleAsync();
        Assert.Equal((AlertTypes.ClientReportFailed, AlertingService.ClientReportAlertSubject(7, "Aurora Retail"), true),
            (alert.AlertType, alert.DomainName, alert.IsResolved));
        Assert.Contains("535 Authentication failed", alert.Message);
    }
}
```

(Construct `AlertingService` the way the existing `AlertingServiceTests` do; `PsaTestSupport.NoOpTicketService()` stands for whatever helper they use for `IPsaTicketService`.)

```csharp
// test/DotMarc.Tests/Reporting/ClientReports/ClientReportRunnerTests.cs
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Email;
using DotMarc.Portal;
using DotMarc.Reporting.ClientReports;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

[Collection("Postgres")]
public sealed class ClientReportRunnerTests : IAsyncLifetime
{
    private sealed class RecordingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedSenderFactory(IEmailSender? sender) : IEmailSenderFactory
    {
        public Task<IEmailSender?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(sender);
    }

    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ClientReportRunnerTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    private ClientReportRunner Runner(IEmailSender? sender, RecordingAlerts? alerts = null)
    {
        var factory = new FakeDbContextFactory(_connectionString);
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero));
        return new ClientReportRunner(factory, new ClientReportBuilder(factory, new PortalBrandLoader(factory), clock), new FixedSenderFactory(sender), alerts ?? new RecordingAlerts(), clock);
    }

    private async Task<int> AddGroupAsync()
    {
        await using var context = CreateContext();
        var group = new Group { Name = "Aurora Retail" };
        context.Domains.Add(new Domain { Name = "aurora-retail.example", IsMonitored = true, Groups = [group] });
        await context.SaveChangesAsync();
        return group.Id;
    }

    private static readonly ReportPeriod March = new(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), ReportPeriodKind.Month);

    [Fact]
    public async Task SendNow_SendsRecordsAndAudits()
    {
        var groupId = await AddGroupAsync();
        var sender = new RecordingSender();
        var alerts = new RecordingAlerts();

        var result = await Runner(sender, alerts).SendNowAsync(TestActors.Admin, groupId, March, ["it@aurora-retail.example"], CancellationToken.None);

        Assert.True(result.Sent);
        Assert.Single(sender.Sent);
        Assert.Contains(groupId, alerts.Resolved);
        await using var verify = CreateContext();
        var delivery = await verify.ClientReportDeliveries.SingleAsync();
        Assert.Equal((ClientReportDeliveryKind.Manual, ClientReportDeliveryStatus.Sent, "admin@example.com"), (delivery.Kind, delivery.Status, delivery.RequestedBy));
        Assert.Equal(AuditActions.ClientReportSent, (await verify.AuditEntries.SingleAsync()).Action);
    }

    [Fact]
    public async Task SendNow_WithEmailOff_SaysSo_AndRecordsNothing()
    {
        var groupId = await AddGroupAsync();

        var result = await Runner(sender: null).SendNowAsync(TestActors.Admin, groupId, March, ["it@aurora-retail.example"], CancellationToken.None);

        Assert.Equal((false, "Email is off. Set it up on Email & reports first."), (result.Sent, result.Message));
        await using var verify = CreateContext();
        Assert.Empty(verify.ClientReportDeliveries);
    }

    [Fact]
    public async Task SendNow_RefusesBadRecipients_BeforeBuildingAnything()
    {
        var groupId = await AddGroupAsync();

        var result = await Runner(new RecordingSender()).SendNowAsync(TestActors.Admin, groupId, March, ["nope"], CancellationToken.None);

        Assert.Equal((false, "nope isn't a valid email address."), (result.Sent, result.Message));
    }

    [Fact]
    public async Task Render_ReturnsAPdfNamedForThePeriod()
    {
        var groupId = await AddGroupAsync();

        var rendered = await Runner(new RecordingSender()).RenderAsync(groupId, March, CancellationToken.None);

        Assert.Equal("Aurora Retail email security report March 2026.pdf", rendered!.Value.FileName);
        Assert.Equal("%PDF-"u8.ToArray(), rendered.Value.Pdf[..5]);
    }
}
```

```csharp
// test/DotMarc.Tests/Reporting/ClientReports/ClientReportDownloadTests.cs
using System.Net;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

[Collection("Postgres")]
public sealed class ClientReportDownloadTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;
    private WebApplicationFactory<Program>? _factory;

    public ClientReportDownloadTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DotMarc", _connectionString);
            builder.UseSetting("Demo:Enabled", "true");
        });
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private async Task<HttpClient> SignInAsync(string persona)
    {
        var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync($"/demo/sign-in/{persona}", content: null);
        return client;
    }

    private async Task<int> GroupIdAsync(string name)
    {
        await using var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);
        return await context.Groups.Where(group => group.Name == name).Select(group => group.Id).SingleAsync();
    }

    [Fact]
    public async Task AnAdmin_DownloadsAPdf()
    {
        using var client = await SignInAsync("admin");
        var groupId = await GroupIdAsync(DotMarc.Demo.DemoDataSeeder.ViewerScopedGroupName);
        var lastMonth = DateTime.UtcNow.AddMonths(-1);
        var start = new DateOnly(lastMonth.Year, lastMonth.Month, 1);

        var response = await client.GetAsync($"/reports/groups/{groupId}/pdf?start={start:yyyy-MM-dd}&end={start.AddMonths(1).AddDays(-1):yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("client")]
    public async Task SomeoneWithoutReportsManage_CantDownload(string persona)
    {
        using var client = await SignInAsync(persona);
        var groupId = await GroupIdAsync(DotMarc.Demo.DemoDataSeeder.ViewerScopedGroupName);

        var response = await client.GetAsync($"/reports/groups/{groupId}/pdf?start=2026-01-01&end=2026-01-31");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ABadRange_IsABadRequest()
    {
        using var client = await SignInAsync("admin");
        var groupId = await GroupIdAsync(DotMarc.Demo.DemoDataSeeder.ViewerScopedGroupName);

        var response = await client.GetAsync($"/reports/groups/{groupId}/pdf?start=2026-03-10&end=2026-03-01");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ClientReportDispatcherTests|FullyQualifiedName~ClientReportRunnerTests|FullyQualifiedName~ClientReportDownloadTests|FullyQualifiedName~ClientReportAlertTests"`
Expected: build FAIL.

- [ ] **Step 3: Implement**

`AlertTypes`: `public const string ClientReportFailed = "ClientReportFailed";` and in `All`: `new(ClientReportFailed, "Client report failed", "A scheduled client report couldn't be sent for 24 hours.", CreatesTicketByDefault: false)`.

`IAlertingService` gains the two methods; `AlertingService` implements them:

```csharp
/// <summary>What a Group's failed report alert is about. Stored where a domain alert stores its domain name; it ends
/// with the Group's id so the alert still resolves after the Group is renamed.</summary>
public static string ClientReportAlertSubject(int groupId, string groupName) =>
    string.Create(CultureInfo.InvariantCulture, $"Client report for {groupName} (group {groupId})");

private static string ClientReportSubjectSuffix(int groupId) => string.Create(CultureInfo.InvariantCulture, $"(group {groupId})");

public async Task RaiseClientReportFailedAsync(int groupId, string groupName, string periodLabel, string error, CancellationToken cancellationToken = default)
{
    await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
    var settings = await NotificationSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
    if (!settings.Enabled)
    {
        return;
    }

    await EnsureAlertAsync(db, settings, ClientReportAlertSubject(groupId, groupName), AlertTypes.ClientReportFailed, "Warning", "Client report failed",
        $"The {periodLabel} report for {groupName} couldn't be sent for 24 hours. Last error: {error}", cancellationToken).ConfigureAwait(false);
}

public async Task ResolveClientReportFailedAsync(int groupId, CancellationToken cancellationToken = default)
{
    await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
    var suffix = ClientReportSubjectSuffix(groupId);
    var subjects = await db.AlertEvents
        .Where(alert => alert.AlertType == AlertTypes.ClientReportFailed && !alert.IsResolved && alert.DomainName.EndsWith(suffix))
        .Select(alert => alert.DomainName)
        .Distinct()
        .ToListAsync(cancellationToken).ConfigureAwait(false);
    foreach (var subject in subjects)
    {
        await ResolveAllCopiesAsync(subject, AlertTypes.ClientReportFailed, cancellationToken).ConfigureAwait(false);
    }
}
```

(Add `using System.Globalization;` if missing.) Add the two methods to `FakeAlertingService` and `NoOpAlertingService` as no-ops returning `Task.CompletedTask`.

```csharp
// src/DotMarc/Reporting/ClientReports/ClientReportDispatcher.cs
using DotMarc.Data;
using DotMarc.Email;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Reporting.ClientReports;

/// <summary>Sends each scheduled Group its latest complete period once it's due. Each Group is handled on its own, so
/// one failing never stops the others. A failed send is retried hourly; once the first attempt is a day old the period
/// is marked failed and "Client report failed" raised. Any later success for the Group resolves that alert.</summary>
public sealed class ClientReportDispatcher(
    IDbContextFactory<DotMarcDbContext> dbFactory,
    ClientReportBuilder builder,
    IEmailSenderFactory senderFactory,
    IAlertingService alerting,
    TimeProvider timeProvider,
    ILogger<ClientReportDispatcher> logger)
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(24);

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        ReportSettings settings;
        List<(GroupReportSchedule Schedule, string GroupName)> schedules;
        await using (var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            settings = await ReportSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
            schedules = (await context.GroupReportSchedules.AsNoTracking()
                .Where(schedule => schedule.Frequency != ReportFrequency.Off)
                .Join(context.Groups, schedule => schedule.GroupId, group => group.Id, (schedule, group) => new { schedule, group.Name })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(row => (row.schedule, row.Name))
                .ToList();
        }

        var zone = ReportSettingsService.ResolveZone(settings.TimeZoneId, logger);
        foreach (var (schedule, groupName) in schedules)
        {
            try
            {
                await RunForGroupAsync(schedule, groupName, zone, settings.SendHour, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Sending the scheduled report for group {GroupName} failed unexpectedly.", groupName);
            }
        }
    }

    private async Task RunForGroupAsync(GroupReportSchedule schedule, string groupName, TimeZoneInfo zone, int sendHour, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var period = ReportPeriods.Previous(schedule.Frequency, zone, now);
        var dueUtc = ReportPeriods.DueUtc(period, zone, sendHour);
        if (now < dueUtc || dueUtc < schedule.StartedUtc)
        {
            return;
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var delivery = await context.ClientReportDeliveries.SingleOrDefaultAsync(candidate =>
            candidate.GroupId == schedule.GroupId && candidate.Kind == ClientReportDeliveryKind.Scheduled
            && candidate.PeriodStart == period.Start && candidate.PeriodEnd == period.End, cancellationToken).ConfigureAwait(false);
        if (delivery is null)
        {
            delivery = new ClientReportDelivery
            {
                GroupId = schedule.GroupId, PeriodStart = period.Start, PeriodEnd = period.End, Kind = ClientReportDeliveryKind.Scheduled,
                Status = ClientReportDeliveryStatus.Pending, Recipients = schedule.Recipients.ToList(),
            };
            context.ClientReportDeliveries.Add(delivery);
        }

        if (delivery.Status != ClientReportDeliveryStatus.Pending
            || (delivery.LastAttemptUtc is { } lastAttempt && now - lastAttempt < RetryInterval))
        {
            return;
        }

        var sender = await senderFactory.GetAsync(cancellationToken).ConfigureAwait(false);
        var hasDomains = await context.Domains.AnyAsync(domain => domain.IsMonitored && domain.Groups.Any(group => group.Id == schedule.GroupId), cancellationToken).ConfigureAwait(false);
        if (sender is null || !hasDomains)
        {
            delivery.Status = ClientReportDeliveryStatus.Skipped;
            delivery.Error = sender is null ? "Email is off" : "No domains";
            delivery.LastAttemptUtc = now;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // Retries go to whoever is on the schedule now, not whoever was on it at the first attempt.
        delivery.Recipients = schedule.Recipients.ToList();
        delivery.Attempts++;
        delivery.FirstAttemptUtc ??= now;
        delivery.LastAttemptUtc = now;
        try
        {
            var report = await builder.BuildAsync(schedule.GroupId, period, zone, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The Group no longer exists.");
            var pdf = ClientReportDocument.Render(report);
            await sender.SendAsync(ClientReportEmail.Compose(report, pdf, delivery.Recipients), cancellationToken).ConfigureAwait(false);
            delivery.Status = ClientReportDeliveryStatus.Sent;
            delivery.SentUtc = now;
            delivery.Error = null;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await alerting.ResolveClientReportFailedAsync(schedule.GroupId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            delivery.Error = exception.Message.Length > 1000 ? exception.Message[..1000] : exception.Message;
            var givingUp = now - delivery.FirstAttemptUtc >= GiveUpAfter;
            if (givingUp)
            {
                delivery.Status = ClientReportDeliveryStatus.Failed;
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (givingUp)
            {
                await alerting.RaiseClientReportFailedAsync(schedule.GroupId, groupName, period.Label, delivery.Error, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                logger.LogWarning(exception, "The {Period} report for group {GroupName} couldn't be sent; trying again in an hour.", period.Label, groupName);
            }
        }
    }
}
```

```csharp
// src/DotMarc/Reporting/ClientReports/ClientReportScheduler.cs
namespace DotMarc.Reporting.ClientReports;

/// <summary>Runs the dispatcher every 15 minutes, starting a minute after startup.</summary>
public sealed class ClientReportScheduler(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<ClientReportScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), timeProvider, stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ClientReportDispatcher>().RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Checking for client reports to send failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
```

```csharp
// src/DotMarc/Reporting/ClientReports/ClientReportRunner.cs
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Email;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Reporting.ClientReports;

public sealed record ManualSendResult(bool Sent, string Message);

/// <summary>On-demand reports from the Reports dialog: send now (recorded as a Manual delivery and audited) and
/// download. Neither affects the schedule.</summary>
public sealed class ClientReportRunner(
    IDbContextFactory<DotMarcDbContext> dbFactory,
    ClientReportBuilder builder,
    IEmailSenderFactory senderFactory,
    IAlertingService alerting,
    TimeProvider timeProvider)
{
    public async Task<TimeZoneInfo> ZoneAsync(CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return ReportSettingsService.ResolveZone((await ReportSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false)).TimeZoneId);
    }

    public async Task<(byte[] Pdf, string FileName)?> RenderAsync(int groupId, ReportPeriod period, CancellationToken cancellationToken)
    {
        var report = await builder.BuildAsync(groupId, period, await ZoneAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return report is null ? null : (ClientReportDocument.Render(report), ClientReportDocument.FileName(report));
    }

    public async Task<ManualSendResult> SendNowAsync(AuditActor actor, int groupId, ReportPeriod period, IReadOnlyList<string> recipients, CancellationToken cancellationToken)
    {
        List<string> tidied;
        try
        {
            tidied = ClientReportService.NormaliseRecipients(recipients);
        }
        catch (ArgumentException exception)
        {
            return new ManualSendResult(false, exception.Message.Split(" (Parameter")[0]);
        }

        if (tidied.Count == 0)
        {
            return new ManualSendResult(false, "Add at least one recipient.");
        }

        var sender = await senderFactory.GetAsync(cancellationToken).ConfigureAwait(false);
        if (sender is null)
        {
            return new ManualSendResult(false, "Email is off. Set it up on Email & reports first.");
        }

        var report = await builder.BuildAsync(groupId, period, await ZoneAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        if (report is null)
        {
            return new ManualSendResult(false, "That Group no longer exists.");
        }

        var now = timeProvider.GetUtcNow();
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var group = await context.Groups.SingleAsync(candidate => candidate.Id == groupId, cancellationToken).ConfigureAwait(false);
        var delivery = new ClientReportDelivery
        {
            GroupId = groupId, PeriodStart = period.Start, PeriodEnd = period.End, Kind = ClientReportDeliveryKind.Manual, Recipients = tidied,
            RequestedBy = actor.Email, Attempts = 1, FirstAttemptUtc = now, LastAttemptUtc = now,
        };
        context.ClientReportDeliveries.Add(delivery);
        try
        {
            await sender.SendAsync(ClientReportEmail.Compose(report, ClientReportDocument.Render(report), tidied), cancellationToken).ConfigureAwait(false);
            delivery.Status = ClientReportDeliveryStatus.Sent;
            delivery.SentUtc = now;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            delivery.Status = ClientReportDeliveryStatus.Failed;
            delivery.Error = exception.Message.Length > 1000 ? exception.Message[..1000] : exception.Message;
        }

        if (delivery.Status == ClientReportDeliveryStatus.Sent)
        {
            AuditLog.Record(context, actor, AuditActions.ClientReportSent, AuditTarget.For(group),
                $"Sent the {period.Label} report for group {group.Name} to {tidied.Count} recipient{(tidied.Count == 1 ? "" : "s")}");
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (delivery.Status == ClientReportDeliveryStatus.Sent)
        {
            await alerting.ResolveClientReportFailedAsync(groupId, cancellationToken).ConfigureAwait(false);
            return new ManualSendResult(true, $"Sent the {period.Label} report to {tidied.Count} recipient{(tidied.Count == 1 ? "" : "s")}.");
        }

        return new ManualSendResult(false, $"Couldn't send the report: {delivery.Error}");
    }
}
```

(Check `AuditActor`'s email property name and use it for `RequestedBy`.)

`Program.cs`:

```csharp
builder.Services.AddScoped<DotMarc.Reporting.ClientReports.ClientReportDispatcher>();
builder.Services.AddScoped<DotMarc.Reporting.ClientReports.ClientReportRunner>();
builder.Services.AddHostedService<DotMarc.Reporting.ClientReports.ClientReportScheduler>();
```

and the endpoint, next to the logo endpoint:

```csharp
// A report as a PDF download, for the Reports dialog. Staff limited to some Groups can only download theirs.
app.MapGet("/reports/groups/{groupId:int}/pdf", async (int groupId, string? start, string? end, HttpContext httpContext,
    DotMarc.Reporting.ClientReports.ClientReportRunner runner, TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    if (!DotMarc.Reporting.ClientReports.ClientReportAccess.MayManage(httpContext.User, groupId))
    {
        return Results.Forbid();
    }

    if (!DateOnly.TryParseExact(start, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var startDay)
        || !DateOnly.TryParseExact(end, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var endDay))
    {
        return Results.BadRequest("start and end must be dates such as 2026-03-01.");
    }

    DotMarc.Reporting.ClientReports.ReportPeriod period;
    try
    {
        period = DotMarc.Reporting.ClientReports.ReportPeriods.FromRange(startDay, endDay, await runner.ZoneAsync(cancellationToken), timeProvider.GetUtcNow());
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(exception.Message.Split(" (Parameter")[0]);
    }

    var rendered = await runner.RenderAsync(groupId, period, cancellationToken);
    return rendered is { } file ? Results.File(file.Pdf, "application/pdf", file.FileName) : Results.NotFound();
}).RequireAuthorization(nameof(Permission.ReportsManage));
```

- [ ] **Step 4: Run the tests and commit**

Run the Step 2 filter (PASS), then `dotnet test test/DotMarc.Tests`.

```bash
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Send client reports on schedule and on demand, and alert when they fail"
```

---

### Task 6: Screens, demo, docs and roadmap

**Files:**
- Create: `src/DotMarc/Components/Pages/EmailAndReportsSettings.razor`, `src/DotMarc/Components/Dialogs/GroupReportsDialog.razor`, `website/docs/client-reports.mdx`
- Modify: `src/DotMarc/Components/Pages/ManageGroups.razor`, `src/DotMarc/Components/Layout/MainLayout.razor` (menu), `src/DotMarc/Demo/DemoDataSeeder.cs`, `website/sidebars.ts`, `website/docs/client-portal.mdx`, `website/docs/getting-started.mdx`, `website/scripts/canny-roadmap.json`
- Test: `test/DotMarc.Tests/Demo/DemoDataSeederTests.cs` (schedule seeded), `test/DotMarc.Tests/Components/ReportScreensTests.cs`

**Interfaces:**
- Consumes: `EmailSettingsService`, `IEmailSenderFactory` (Task 1); `ReportSettingsService`, `ClientReportService`, `ReportPeriods`, `ClientReportAccess` (Task 2); `ClientReportRunner` (Task 5); `BrandingSettingsPage.LogoHelp` pattern and `FieldWithHelp` (existing).
- Produces: route `/reports/settings` (policy `ReportsManage`); the Reports dialog; menu entry "Email & reports".

- [ ] **Step 1: Write the failing tests**

```csharp
// test/DotMarc.Tests/Components/ReportScreensTests.cs
using System.Net;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DotMarc.Tests.Components;

[Collection("Postgres")]
public sealed class ReportScreensTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private IAsyncDisposable? _cleanup;
    private WebApplicationFactory<Program>? _factory;

    public ReportScreensTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (var connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DotMarc", connectionString);
            builder.UseSetting("Demo:Enabled", "true");
        });
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private async Task<HttpClient> SignInAsync(string persona)
    {
        var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync($"/demo/sign-in/{persona}", content: null);
        return client;
    }

    [Fact]
    public async Task TheSettingsPage_SaysTheDemoDoesntSendEmail()
    {
        using var client = await SignInAsync("admin");

        var html = await client.GetStringAsync("/reports/settings");

        Assert.Contains("Email &amp; reports", html);
        Assert.Contains("The demo doesn", html); // "doesn't", apostrophe encoded
    }

    [Fact]
    public async Task AViewer_CantOpenTheSettingsPage()
    {
        using var client = await SignInAsync("viewer");

        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/reports/settings")).StatusCode);
    }
}
```

In `DemoDataSeederTests`, add:

```csharp
[Fact]
public async Task AuroraRetail_HasAMonthlyReportSchedule()
{
    // seed as the existing tests do, then:
    var schedule = await context.GroupReportSchedules.SingleAsync();
    Assert.Equal(ReportFrequency.Monthly, schedule.Frequency);
    Assert.Equal(["reports@aurora-retail.example"], schedule.Recipients);
}
```

(Follow the file's existing seeding and context set-up.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ReportScreensTests|FullyQualifiedName~DemoDataSeederTests"`
Expected: FAIL (route missing; no schedule seeded).

- [ ] **Step 3: Implement**

**`EmailAndReportsSettings.razor`** (`@page "/reports/settings"`, `[Authorize(Policy = "ReportsManage")]`, MainLayout, title "dotMARC - Email & reports", heading "Email & reports" with a `DocsLink` to `https://dotmarc.app/docs/client-reports`):
- Inject `IOptions<DemoOptions>`; in the demo, show `<MudAlert Severity="Severity.Info">The demo doesn't send email. Reports can still be downloaded from Manage groups.</MudAlert>` and render the email fields disabled.
- **Email** paper: `MudSelect` provider (Off, Microsoft Graph, SMTP). For Graph and SMTP: From address (prefilled with the configured `GraphOptions.MailboxAddress` when switching to Graph and it is empty; read it through `IServiceProvider.GetService<IOptions<GraphOptions>>()`, which is absent in the demo). For SMTP only: From name (helper: "Leave empty to use your product name."), host, port (`MudNumericField`), security (`MudSelect`: STARTTLS, TLS on connect, None), username, password (`InputType.Password`, write-only, `HelperText` "A password is saved. Leave blank to keep it." when `SmtpPasswordConfigured`). Every field wrapped in `FieldWithHelp` with a one-sentence explanation; Graph's from address help: "Graph sends as this mailbox. It must be a mailbox in your Microsoft 365 tenant, and the app registration needs the Mail.Send permission."
- **Save email settings** calls `EmailSettingsService.SaveAsync(db, actor, secretStore, _email, _newPassword)`; an `ArgumentException` shows its message (split off " (Parameter") as an error snackbar.
- **Send test email**: a text field for an address and a button; it uses the saved settings (caption: "Uses the saved settings. Save first if you've changed them."): `var sender = await SenderFactory.GetAsync(ct)`; null shows "Email is off."; otherwise send `new EmailMessage([address], "dotMARC test email", "<p>This is a test email from dotMARC. Email is working.</p>", "This is a test email from dotMARC. Email is working.", [])` and show "Test email sent to {address}." or the `EmailSendException` message.
- **Reports** paper: `MudAutocomplete<string>` for the time zone searching `ReportSettingsService.AvailableZones()` (case-insensitive contains), `MudSelect<int>` send hour 0 to 23 shown as "06:00", **Save report settings** calling `ReportSettingsService.SaveAsync`.

**`MainLayout.razor`**: in the Manage menu add, inside a new `<AuthorizeView Policy="ReportsManage">`, `<MudMenuItem Href="/reports/settings" Icon="@Icons.Material.Filled.Email">Email & reports</MudMenuItem>`.

**`GroupReportsDialog.razor`** (`[Parameter] int GroupId`, `[Parameter] string GroupName`; opened from Manage groups like the Branding dialog with `MaxWidth.Medium`):
- On load: `ClientReportAccess.MayManage(user, GroupId)` (otherwise show "You can't manage reports for this Group." and nothing else), the schedule, `ClientReportService.SuggestRecipientsAsync`, `ClientReportService.ListDeliveriesAsync(db, GroupId, 10)`, the report settings and zone, and whether email is on (`await SenderFactory.GetAsync(ct) is not null`).
- When email is off: `MudAlert` "Email is off, so reports can't be sent. Set it up on Email & reports." with a link to `/reports/settings` (hidden from users without `ReportsManage`, which everyone in this dialog has).
- **Schedule**: `MudSelect` frequency (Off, Weekly, Monthly, Quarterly); recipients as `MudChip`s with a close icon, added through a `MudAutocomplete<string>` whose `SearchFunc` returns the suggestions containing the typed text plus the typed text itself when it looks like an address; pressing Enter or choosing adds a chip; invalid addresses show "{address} isn't a valid email address." and aren't added; more than 25 shows "A report can go to at most 25 recipients.". **Save schedule** calls `ClientReportService.SetScheduleAsync`; errors show their message.
- **Status line**: when scheduled, compute `var next = ReportPeriods.Previous(frequency, zone, now)`; if it's already sent (a Sent scheduled delivery for it) or due before `StartedUtc`, use the following period (`PreviousForComparison` inverted: the next period of the same kind after `next`, built with `ReportPeriods.Week/Month/Quarter(next.End.AddDays(1))`); show "Next report: {label}, sends {DueUtc converted to the zone, d MMMM yyyy HH:mm} ({zone id})". Otherwise "Not scheduled". Below it, the latest scheduled delivery: "Last sent {date}" or, in red, "Last report failed: {error}".
- **Send now / Download**: a period picker: `MudSelect` kind (Week, Month, Quarter, Custom range); for the first three a `MudSelect<ReportPeriod>` of `ReportPeriods.Recent(kind, zone, now, 12)` labelled by `Label`; for Custom two `MudDatePicker`s. Recipients for Send now are a separate chip list prefilled from the schedule. **Send now** calls `Runner.SendNowAsync(actor, GroupId, period, recipients, ct)` and shows the result message (success or error snackbar), then reloads history. **Download PDF** is an anchor `href="/reports/groups/{GroupId}/pdf?start={period.Start:yyyy-MM-dd}&end={period.End:yyyy-MM-dd}"` with `target="_blank"`; for a custom range validate first with `ReportPeriods.FromRange` and show its message instead of linking when invalid.
- **History** table: Period (label from `ReportPeriods.FromRange` on the stored days, falling back to the dates), When (local), Kind ("Scheduled" or "Manual by {RequestedBy}"), Recipients (count, with the list in a tooltip), Outcome (Sent, Failed with error tooltip, Skipped with reason, Pending "retrying").

**`ManageGroups.razor`**: inside `<AuthorizeView Policy="ReportsManage">`, a **Reports** button next to Branding opening the dialog, and a chip: the frequency ("Monthly") when scheduled, or `Color.Error` "Report failed" when the latest scheduled delivery for the Group is Failed. Load both with the groups: `db.GroupReportSchedules.Where(s => s.Frequency != ReportFrequency.Off).ToDictionaryAsync(s => s.GroupId, s => s.Frequency)` and the Group ids whose latest scheduled delivery is Failed:

```csharp
_failedReportGroupIds = (await db.ClientReportDeliveries
    .Where(delivery => delivery.Kind == ClientReportDeliveryKind.Scheduled)
    .GroupBy(delivery => delivery.GroupId)
    .Select(deliveries => deliveries.OrderByDescending(delivery => delivery.PeriodStart).First())
    .Where(latest => latest.Status == ClientReportDeliveryStatus.Failed)
    .Select(latest => latest.GroupId)
    .ToListAsync()).ToHashSet();
```

Refresh both after the dialog closes, with the same "Couldn't refresh" warning pattern the Branding chips use.

**`DemoDataSeeder`**: after the branding seed, add
`context.GroupReportSchedules.Add(new GroupReportSchedule { GroupId = groupsByName[ViewerScopedGroupName].Id, Frequency = ReportFrequency.Monthly, Recipients = ["reports@aurora-retail.example"], StartedUtc = DateTimeOffset.UtcNow });`

**Docs**: `website/docs/client-reports.mdx` (`sidebar_position: 7`), covering, in plain language with no em dashes:
- What a report contains (cover, summary, trend, per-domain sections, what to do next) and who it's for.
- Setting up email: Microsoft Graph (add the **Mail.Send** application permission to the existing app registration and grant admin consent; restrict it to the sending mailbox with an Exchange Online application access policy, with the `New-ApplicationAccessPolicy` command shown; the from address must be that mailbox) or SMTP (host, port, security, username and password; works with relay services). The test email.
- Time zone and send hour; when weekly, monthly and quarterly reports go out; that turning a schedule on doesn't send a backlog.
- Schedules and recipients in the Reports dialog on Manage groups; suggestions from the Group's grants; at most 25 recipients.
- Send now and Download PDF for any past week, month, quarter or custom range up to 366 days; manual sends don't affect the schedule.
- Logos: PNG or JPEG appear in reports; an SVG-only brand shows the product name.
- Failures: hourly retries for 24 hours, then the "Client report failed" alert through your alert channels; the Manage groups chip; delivery history.
- The `ReportsManage` permission.

Add `'client-reports'` after `'client-portal'` in `website/sidebars.ts`; link it from `client-portal.mdx` ("Send clients a regular report too: see [Client reports](./client-reports.mdx).") and from `getting-started.mdx` where Graph permissions are listed ("To send client reports through Graph, also add Mail.Send; see [Client reports](./client-reports.mdx)."). In `canny-roadmap.json`, set "Add scheduled client report export (PDF and email digest)" to `"status": "complete"` with details:
- "Each Group can get a branded PDF report weekly, monthly or quarterly, emailed through Microsoft Graph or SMTP: a summary of every domain's status and pass rate with the change from the last period, a trend chart, per-domain policy, health, top senders and what receivers did, and a plain-language list of what to do next."
- "Reports can also be sent or downloaded on demand for any past period. Periods follow your chosen time zone, failed sends are retried for a day before raising an alert, and every delivery is recorded."

- [ ] **Step 4: Run, browser check, docs build, commit**

Run the Step 2 filter (PASS) and `dotnet test test/DotMarc.Tests` (all pass). Docs: `cd website && npx docusaurus build` (build to a scratch `--out-dir` if `website/build` is locked); no broken links.

Browser check with the demo (`Demo__Enabled=true`, `ASPNETCORE_ENVIRONMENT=Development`, `ASPNETCORE_URLS=http://localhost:54704`, `dotnet exec bin/Debug/net10.0/DotMarc.dll` from `src/DotMarc`), as Demo Admin:
- Manage > Email & reports shows the demo note and disabled email fields; the time zone search finds "Europe/London"; saving report settings works.
- Manage groups shows Aurora Retail's "Monthly" chip; its Reports dialog shows the schedule, next report line and history; adding an invalid recipient is refused.
- Download PDF for last month opens a PDF (save it under `.playwright-mcp/` and open it with the Read tool to look at the pages: cover with "Aurora Retail Ltd", summary, chart, domain sections, next steps).
- Send now says "Email is off. Set it up on Email & reports first."
- As Demo Viewer, `/reports/settings` is refused and there's no Reports button.
Stop only the process you started.

```bash
git add -A src/DotMarc test/DotMarc.Tests website
git commit -m "Add the Email & reports page, the Reports dialog and client report docs"
```
