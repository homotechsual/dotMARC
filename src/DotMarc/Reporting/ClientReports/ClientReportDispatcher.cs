using DotMarc.Data;
using DotMarc.Email;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

    /// <summary>Only one instance sends reports at a time (two replicas, or two revisions overlapping during a deploy),
    /// so a report is never sent twice. Same pattern and key range as PollingService's leader locks.</summary>
    internal const long ClientReportLeaderLockKey = 84_200_025;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        string connectionString;
        await using (var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            connectionString = context.Database.GetConnectionString()
                ?? throw new InvalidOperationException("DotMarcDbContext has no connection string configured.");
        }

        // A transaction-scoped advisory lock, so it can never outlive the connection it was taken on.
        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var lockTransaction = await lockConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", ClientReportLeaderLockKey);
            if (!(bool)(await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
            {
                logger.LogDebug("Another instance is sending client reports; skipping this run.");
                return;
            }
        }

        try
        {
            await RunLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await lockTransaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RunLockedAsync(CancellationToken cancellationToken)
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

        // Claim the attempt before sending, so the period is on record even if this instance stops part-way.
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        Exception? failure = null;
        try
        {
            var report = await builder.BuildAsync(schedule.GroupId, period, zone, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The Group no longer exists.");
            var pdf = ClientReportDocument.Render(report);
            await sender.SendAsync(ClientReportEmail.Compose(report, pdf, delivery.Recipients), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failure = exception;
        }

        if (failure is null)
        {
            // The email has gone; nothing after this point may record it as failed.
            delivery.Status = ClientReportDeliveryStatus.Sent;
            delivery.SentUtc = now;
            delivery.Error = null;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await alerting.ResolveClientReportFailedAsync(schedule.GroupId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Sent the {Period} report for group {GroupName}, but couldn't close its failure alert.", period.Label, groupName);
            }

            return;
        }

        delivery.Error = failure.Message.Length > 1000 ? failure.Message[..1000] : failure.Message;
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
            logger.LogWarning(failure, "The {Period} report for group {GroupName} couldn't be sent; trying again in an hour.", period.Label, groupName);
        }
    }
}
