using DotMarc.Data;
using DotMarc.Dns;
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

    /// <summary>Arbitrary fixed key for this service's DMARC-check advisory lock — distinct from
    /// PollingLeaderLockKey so the mailbox-poll cycle and the DMARC DNS-check cycle run under
    /// independent locks rather than being forced to share the same leader/timing.</summary>
    internal const long DmarcCheckLeaderLockKey = 84_200_003;

    private sealed record PollCycleCounts(int MessagesChecked, int ReportsParsed, int ParseFailures);

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
                    var context = scope.ServiceProvider.GetRequiredService<DotMarcDbContext>();

                    try
                    {
                        var graphClient = scope.ServiceProvider.GetRequiredService<IGraphMailboxClient>();
                        await RunPollCycleAsync(graphClient, context, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Mailbox poll cycle failed; will retry next interval.");
                    }

                    try
                    {
                        // The two cycles share this one scoped DotMarcDbContext. If the poll cycle
                        // above failed, it may have left half-built entities tracked as Added/Modified
                        // on this context; without clearing the tracker first, the DMARC cycle's own
                        // SaveChangesAsync could flush those leftovers alongside its own changes.
                        context.ChangeTracker.Clear();
                        var dmarcChecker = scope.ServiceProvider.GetRequiredService<IDmarcDnsChecker>();
                        await RunDmarcCheckCycleAsync(context, dmarcChecker, _options!.MailboxAddress, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "DMARC check cycle failed; will retry next interval.");
                    }
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
            PollCycleCounts counts;
            try
            {
                counts = await PollOnceAsync(graphClient, context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                context.ChangeTracker.Clear();
                try
                {
                    await RecordPollCycleAsync(context, new PollCycleCounts(0, 0, 0), succeeded: false, ex.Message, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception recordEx)
                {
                    _logger.LogWarning(recordEx, "Could not record the failed poll cycle; the original failure follows.");
                }
                throw;
            }

            await RecordPollCycleAsync(context, counts, succeeded: true, errorMessage: null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await lockTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Runs a DMARC DNS status check for every domain whose last check (DmarcCheckedUtc)
    /// is null or more than 24 hours old — independent of, and under a separate advisory lock from,
    /// the mailbox poll cycle above, since the two concerns don't need to share timing or a leader.
    /// A domain whose check itself fails (network error, Cloudflare unreachable) is left with its
    /// prior status/timestamp untouched and simply retried next cycle — matching this service's
    /// existing "leave it, retry later" policy for other kinds of per-item failure.</summary>
    internal async Task RunDmarcCheckCycleAsync(DotMarcDbContext context, IDmarcDnsChecker dmarcChecker, string mailboxAddress, CancellationToken cancellationToken)
    {
        var connectionString = context.Database.GetConnectionString()
            ?? throw new InvalidOperationException("DotMarcDbContext has no connection string configured.");

        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var lockTransaction = await lockConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        bool acquired;
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", DmarcCheckLeaderLockKey);
            acquired = (bool)(await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (!acquired)
        {
            _logger.LogDebug("Another instance already holds the DMARC-check lock for this cycle; skipping.");
            return;
        }

        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            var staleDomains = await context.Domains
                .Where(d => d.DmarcCheckedUtc == null || d.DmarcCheckedUtc < cutoff)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var anyUpdated = false;
            foreach (var domain in staleDomains)
            {
                DmarcCheckResult result;
                try
                {
                    result = await dmarcChecker.CheckAsync(domain.Name, mailboxAddress, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DMARC DNS check failed for {Domain}; will retry next cycle.", domain.Name);
                    continue;
                }

                domain.DmarcCheckStatus = result.Status;
                domain.DmarcCheckedUtc = DateTimeOffset.UtcNow;
                domain.DmarcCheckDetail = result.Detail;
                anyUpdated = true;
            }

            if (anyUpdated)
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await lockTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes one PollCycle row for a cycle that actually ran (never for one skipped due
    /// to the leader lock — see RunPollCycleAsync). Rollup of stale rows happens here too, inline,
    /// rather than as a separate scheduled job — see RollUpStalePollCyclesAsync.</summary>
    private static async Task RecordPollCycleAsync(DotMarcDbContext context, PollCycleCounts counts, bool succeeded, string? errorMessage, CancellationToken cancellationToken)
    {
        context.PollCycles.Add(new PollCycle
        {
            PolledUtc = DateTimeOffset.UtcNow,
            MessagesChecked = counts.MessagesChecked,
            ReportsParsed = counts.ReportsParsed,
            ParseFailures = counts.ParseFailures,
            Succeeded = succeeded,
            ErrorMessage = errorMessage
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await RollUpStalePollCyclesAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Folds any PollCycle row belonging to a UTC calendar day more than 7 days in the
    /// past into that day's PollCycleDailySummary, then deletes the raw rows. internal (not
    /// private) so tests can call it directly against hand-seeded, backdated rows — the only
    /// production caller, RecordPollCycleAsync, always writes PolledUtc as "now," so there's no
    /// other way to exercise the &gt;7-day-old path deterministically. Anchored to a calendar-day
    /// boundary rather than a rolling timestamp: a day is only eligible once every one of its rows
    /// is already more than 7 days old, so it's only ever rolled up once, with nothing to merge
    /// across passes.</summary>
    internal static async Task RollUpStalePollCyclesAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var cutoffUtc = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(-7);

        var staleRows = await context.PollCycles
            .Where(p => p.PolledUtc < cutoffUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (staleRows.Count == 0)
        {
            return;
        }

        foreach (var group in staleRows.GroupBy(p => DateOnly.FromDateTime(p.PolledUtc.UtcDateTime)))
        {
            var summary = await context.PollCycleDailySummaries
                .SingleOrDefaultAsync(s => s.Date == group.Key, cancellationToken)
                .ConfigureAwait(false);

            if (summary is null)
            {
                summary = new PollCycleDailySummary { Date = group.Key };
                context.PollCycleDailySummaries.Add(summary);
            }

            summary.TotalCycles += group.Count();
            summary.SuccessfulCycles += group.Count(p => p.Succeeded);
            summary.FailedCycles += group.Count(p => !p.Succeeded);
            summary.TotalMessagesChecked += group.Sum(p => p.MessagesChecked);
            summary.TotalReportsParsed += group.Sum(p => p.ReportsParsed);
            summary.TotalParseFailures += group.Sum(p => p.ParseFailures);
        }

        context.PollCycles.RemoveRange(staleRows);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<PollCycleCounts> PollOnceAsync(IGraphMailboxClient graphClient, DotMarcDbContext context, CancellationToken cancellationToken)
    {
        var messages = await graphClient.GetUnreadMessagesAsync(cancellationToken).ConfigureAwait(false);

        var reportsParsed = 0;
        var parseFailures = 0;

        foreach (var message in messages)
        {
            if (!message.HasAttachments)
            {
                continue;
            }

            var alreadyProcessed = await context.ProcessedMessages.AnyAsync(m => m.GraphMessageId == message.Id, cancellationToken).ConfigureAwait(false);
            if (alreadyProcessed)
            {
                // Already turned into a Report on an earlier poll; Graph's isRead flag just hasn't
                // caught up (e.g. a prior MarkAsReadAsync attempt failed). Retry only the cheap
                // mark-as-read call — the ProcessedMessage row already proves re-fetching and
                // re-parsing the attachment would be wasted work.
                try
                {
                    await graphClient.MarkAsReadAsync(message.Id, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception markReadEx)
                {
                    _logger.LogWarning(markReadEx, "Message {MessageId} was already processed; retrying mark-as-read failed again.", message.Id);
                }
                continue;
            }

            try
            {
                await ProcessMessageAsync(graphClient, context, message, cancellationToken).ConfigureAwait(false);
                reportsParsed++;
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
                parseFailures++;
            }
        }

        return new PollCycleCounts(messages.Count, reportsParsed, parseFailures);
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
                await RecordProcessedMessageAsync(context, message.Id, cancellationToken).ConfigureAwait(false);

                // The report is safely committed and recorded as processed at this point — a
                // failure here won't cause re-fetching/re-parsing on the next poll (see
                // PollOnceAsync's ProcessedMessages check), only a cheap mark-as-read retry. Marking
                // the message read is a separate Graph call — a failure here is NOT an unparseable
                // message, so it must not fall into the ParseFailure path below.
                try
                {
                    await graphClient.MarkAsReadAsync(message.Id, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception markReadEx)
                {
                    _logger.LogWarning(markReadEx,
                        "Report for message {MessageId} was stored successfully, but marking it read failed. " +
                        "It will be retried next poll without re-parsing.",
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
            var nextSortOrder = (await context.Domains.MaxAsync(d => (int?)d.SortOrder, cancellationToken).ConfigureAwait(false) ?? -1) + 1;
            domain = new Domain { Name = parsed.Domain, FirstSeenUtc = DateTimeOffset.UtcNow, SortOrder = nextSortOrder };
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

    /// <summary>Records that a mailbox message has been successfully turned into a stored Report,
    /// so PollOnceAsync can skip re-fetching and re-parsing it on a later poll if Graph's isRead
    /// flag never got set (see <see cref="ProcessedMessage"/>). Also clears any ParseFailure row
    /// left over from an earlier failed attempt at this same message — otherwise a message that
    /// failed once and later succeeded would keep showing as "unparseable" on the Parse Failures
    /// page forever, even though it's since been stored correctly.</summary>
    private static async Task RecordProcessedMessageAsync(DotMarcDbContext context, string graphMessageId, CancellationToken cancellationToken)
    {
        context.ProcessedMessages.Add(new ProcessedMessage { GraphMessageId = graphMessageId, ProcessedUtc = DateTimeOffset.UtcNow });

        var staleFailure = await context.ParseFailures
            .SingleOrDefaultAsync(f => f.GraphMessageId == graphMessageId, cancellationToken)
            .ConfigureAwait(false);
        if (staleFailure is not null)
        {
            context.ParseFailures.Remove(staleFailure);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Another attempt at this same message (e.g. a retried poll racing this one) already
            // recorded it first — same outcome either way, nothing further to do.
            context.ChangeTracker.Clear();
        }
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
