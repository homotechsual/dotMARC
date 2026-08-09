using DotMarc.Data;
using DotMarc.Graph;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotMarc.Ingestion;

public sealed class PollingService : BackgroundService
{
    private readonly IGraphMailboxClient? _graphClient;
    private readonly DotMarcDbContext? _context;
    private readonly ILogger<PollingService> _logger;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly GraphOptions? _options;

    /// <summary>Constructor used by tests: operates directly against an already-open
    /// IGraphMailboxClient and DotMarcDbContext, bypassing DI scoping entirely.</summary>
    public PollingService(IGraphMailboxClient graphClient, DotMarcDbContext context, ILogger<PollingService> logger)
    {
        _graphClient = graphClient;
        _context = context;
        _logger = logger;
    }

    /// <summary>Constructor used by the host: creates a fresh DI scope per poll, since
    /// DotMarcDbContext is registered scoped and this service itself is a singleton.</summary>
    public PollingService(IServiceScopeFactory scopeFactory, IOptions<GraphOptions> options, ILogger<PollingService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options?.PollIntervalSeconds ?? 300);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_scopeFactory is not null)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var graphClient = scope.ServiceProvider.GetRequiredService<IGraphMailboxClient>();
                    var context = scope.ServiceProvider.GetRequiredService<DotMarcDbContext>();
                    await PollOnceAsync(graphClient, context, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Poll cycle failed; will retry next interval.");
            }

            await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Runs a single poll cycle against the constructor-supplied client/context (test
    /// entry point).</summary>
    public Task PollOnceAsync(CancellationToken cancellationToken) =>
        PollOnceAsync(_graphClient!, _context!, cancellationToken);

    private async Task PollOnceAsync(IGraphMailboxClient graphClient, DotMarcDbContext context, CancellationToken cancellationToken)
    {
        var messages = await graphClient.GetUnreadMessagesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var message in messages)
        {
            if (!message.HasAttachments)
            {
                continue;
            }

            try
            {
                await ProcessMessageAsync(graphClient, context, message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse message {MessageId}; leaving unread for retry.", message.Id);
                context.ParseFailures.Add(new ParseFailure
                {
                    GraphMessageId = message.Id,
                    Reason = ex.Message,
                    OccurredUtc = DateTimeOffset.UtcNow
                });
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessMessageAsync(IGraphMailboxClient graphClient, DotMarcDbContext context, MailboxMessage message, CancellationToken cancellationToken)
    {
        var attachments = await graphClient.GetAttachmentsAsync(message.Id, cancellationToken).ConfigureAwait(false);
        if (attachments.Count == 0)
        {
            throw new InvalidDataException("Message has no attachments despite hasAttachments=true.");
        }

        // A message may carry more than one attachment; the first one that decompresses and
        // parses successfully is treated as the report. Any earlier attachment that fails is
        // simply not the report (e.g. an inline logo image) — only the message as a whole failing
        // to yield any valid report is a genuine ParseFailure.
        Exception? lastError = null;
        foreach (var attachment in attachments)
        {
            try
            {
                var decompressed = ReportDecompressor.Decompress(attachment.ContentBytes);
                var parsed = DmarcReportParser.Parse(decompressed);
                await StoreReportAsync(context, parsed, System.Text.Encoding.UTF8.GetString(decompressed), cancellationToken).ConfigureAwait(false);
                await graphClient.MarkAsReadAsync(message.Id, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidDataException("No attachment could be parsed as a DMARC report.");
    }

    private static async Task StoreReportAsync(DotMarcDbContext context, ParsedReport parsed, string rawXml, CancellationToken cancellationToken)
    {
        var domain = await context.Domains.SingleOrDefaultAsync(d => d.Name == parsed.Domain, cancellationToken).ConfigureAwait(false);
        if (domain is null)
        {
            domain = new Domain { Name = parsed.Domain, FirstSeenUtc = DateTimeOffset.UtcNow };
            context.Domains.Add(domain);
        }
        domain.LastReportReceivedUtc = DateTimeOffset.UtcNow;

        var report = new Report
        {
            Domain = domain,
            ReportingOrg = parsed.ReportingOrg,
            ReportId = parsed.ReportId,
            DateRangeBeginUtc = parsed.DateRangeBeginUtc,
            DateRangeEndUtc = parsed.DateRangeEndUtc,
            RawXml = rawXml,
            ReceivedUtc = DateTimeOffset.UtcNow
        };

        foreach (var record in parsed.Records)
        {
            report.Records.Add(new ReportRecord
            {
                SourceIp = record.SourceIp,
                MessageCount = record.MessageCount,
                Disposition = Enum.Parse<DispositionResult>(record.Disposition),
                SpfResult = Enum.Parse<AuthResult>(record.SpfResult),
                DkimResult = Enum.Parse<AuthResult>(record.DkimResult),
                HeaderFrom = record.HeaderFrom
            });
        }

        context.Reports.Add(report);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
