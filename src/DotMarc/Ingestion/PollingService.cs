using DotMarc.Data;
using DotMarc.Graph;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DotMarc.Ingestion;

public sealed class PollingService : BackgroundService
{
    /// <summary>Arbitrary fixed key for this service's Postgres advisory lock. Multiple replicas
    /// may run this service concurrently; only the one that acquires this transaction-scoped lock
    /// for a given cycle actually polls the mailbox — others skip that cycle and try again next
    /// interval. Prevents duplicate Graph calls and duplicate-report races when scaled beyond one
    /// replica.</summary>
    internal const long PollingLeaderLockKey = 84_200_001;

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
    /// DotMarcDbContext is registered scoped and this service itself is a singleton.
    /// <see cref="ActivatorUtilitiesConstructorAttribute"/> disambiguates constructor selection
    /// for the DI container: both IGraphMailboxClient and DotMarcDbContext are also registered
    /// in DI (Tasks 5 and 2 respectively), so without this attribute the other constructor's
    /// parameters would all be resolvable too, and the container would throw on activation
    /// rather than pick one.</summary>
    [ActivatorUtilitiesConstructor]
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
                    await RunPollCycleAsync(graphClient, context, stoppingToken).ConfigureAwait(false);
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

    /// <summary>Wraps <see cref="PollOnceAsync(IGraphMailboxClient, DotMarcDbContext,
    /// CancellationToken)"/> in the leader-election lock: tries to acquire
    /// <see cref="PollingLeaderLockKey"/> as a transaction-scoped advisory lock on a dedicated
    /// connection, skips the cycle entirely if another replica already holds it, and otherwise
    /// polls and then releases the lock by committing that transaction. The lock is
    /// transaction-scoped rather than session-scoped specifically so it can never outlive a
    /// pooled connection being returned to the pool without an explicit unlock.</summary>
    internal async Task RunPollCycleAsync(IGraphMailboxClient graphClient, DotMarcDbContext context, CancellationToken cancellationToken)
    {
        var connectionString = context.Database.GetConnectionString()
            ?? throw new InvalidOperationException("DotMarcDbContext has no connection string configured.");

        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var lockTransaction = await lockConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        bool acquired;
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", PollingLeaderLockKey);
            acquired = (bool)(await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (!acquired)
        {
            _logger.LogDebug("Another instance already holds the polling lock for this cycle; skipping.");
            return;
        }

        try
        {
            await PollOnceAsync(graphClient, context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await lockTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

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

                // A prior SaveChangesAsync failure earlier in this method (e.g. inside
                // StoreReportAsync) can leave half-built Domain/Report/ReportRecord entities
                // tracked as Added on this shared context. Without clearing the tracker first, the
                // save below would re-attempt those leftover entities alongside the ParseFailure
                // and could throw again here, uncaught, aborting the rest of the poll cycle.
                context.ChangeTracker.Clear();
                await RecordParseFailureAsync(context, message.Id, ex.Message, cancellationToken).ConfigureAwait(false);
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

                // The report is safely committed at this point (StoreReportAsync's own
                // duplicate check makes a re-attempt of this same message harmless). Marking the
                // message read is a separate, transient Graph call — a failure here is NOT an
                // unparseable message, so it must not fall into the ParseFailure path below. Log
                // it distinctly and let the message get picked up again next poll.
                try
                {
                    await graphClient.MarkAsReadAsync(message.Id, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception markReadEx)
                {
                    _logger.LogWarning(markReadEx,
                        "Report for message {MessageId} was stored successfully, but marking it read failed. " +
                        "It will be reprocessed next poll; the duplicate-report check makes that safe.",
                        message.Id);
                }

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

        // domain.Id is only non-zero for an already-persisted domain (EF Core leaves the CLR
        // property at its default until SaveChanges assigns the real value), so a brand-new
        // domain can never already have a report — skip the lookup for that case.
        var isDuplicate = domain.Id != 0 && await context.Reports.AnyAsync(
            r => r.DomainId == domain.Id && r.ReportingOrg == parsed.ReportingOrg && r.ReportId == parsed.ReportId,
            cancellationToken).ConfigureAwait(false);

        if (isDuplicate)
        {
            // Same report already stored from an earlier attempt at this message (see the
            // MarkAsReadAsync-failure handling in ProcessMessageAsync). Nothing to insert — the
            // caller still retries marking the message read.
            return;
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

    /// <summary>Inserts a new ParseFailure row for a never-before-failed message, or updates the
    /// existing row's attempt count/reason/timestamp for a repeat failure — keeps a permanently
    /// unparseable message from growing a new row every poll cycle forever (see
    /// <see cref="ParseFailure"/>). No auto-give-up policy here: the message is retried
    /// indefinitely, only the bookkeeping row is deduplicated.</summary>
    private static async Task RecordParseFailureAsync(DotMarcDbContext context, string graphMessageId, string reason, CancellationToken cancellationToken)
    {
        var existing = await context.ParseFailures
            .SingleOrDefaultAsync(f => f.GraphMessageId == graphMessageId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            context.ParseFailures.Add(new ParseFailure
            {
                GraphMessageId = graphMessageId,
                Reason = reason,
                AttemptCount = 1,
                LastAttemptedUtc = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.AttemptCount++;
            existing.Reason = reason;
            existing.LastAttemptedUtc = DateTimeOffset.UtcNow;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
