using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Reporting.ClientReports;

/// <summary>A Group's report schedule and delivery history.</summary>
public static class ClientReportService
{
    public static Task<GroupReportSchedule?> GetScheduleAsync(DotMarcDbContext context, int groupId, CancellationToken cancellationToken = default) =>
        context.GroupReportSchedules.AsNoTracking().SingleOrDefaultAsync(schedule => schedule.GroupId == groupId, cancellationToken);

    public static async Task SetScheduleAsync(DotMarcDbContext context, AuditActor actor, int groupId, ReportFrequency frequency, IReadOnlyList<string> recipients, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        var tidied = ReportRecipients.Normalise(recipients);
        if (frequency != ReportFrequency.Off && tidied.Count == 0)
        {
            throw new ArgumentException("Add at least one recipient, or turn the schedule off.", nameof(recipients));
        }

        var group = await context.Groups.SingleAsync(candidate => candidate.Id == groupId, cancellationToken).ConfigureAwait(false);
        var existing = await context.GroupReportSchedules.SingleOrDefaultAsync(schedule => schedule.GroupId == groupId, cancellationToken).ConfigureAwait(false);
        var previousFrequency = existing?.Frequency ?? ReportFrequency.Off;
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
                existing.StartedUtc = (timeProvider ?? TimeProvider.System).GetUtcNow();
            }

            existing.Frequency = frequency;
            existing.Recipients = tidied;
        }

        // A period still retrying belongs to the old frequency, which the scheduler no longer looks at; close it off so it
        // doesn't sit as "retrying" for ever, never giving up or raising its alert.
        if (previousFrequency != frequency)
        {
            var retrying = await context.ClientReportDeliveries
                .Where(delivery => delivery.GroupId == groupId && delivery.Kind == ClientReportDeliveryKind.Scheduled && delivery.Status == ClientReportDeliveryStatus.Pending)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var delivery in retrying)
            {
                delivery.Status = ClientReportDeliveryStatus.Skipped;
                delivery.Error = "The schedule changed";
            }
        }

        AuditLog.Record(context, actor, AuditActions.GroupReportScheduleChanged, AuditTarget.For(group), $"Changed the report schedule for group {group.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Emails of the grants limited to this Group (its client contacts and Group-scoped staff), for the
    /// recipient field's suggestions.</summary>
    public static async Task<IReadOnlyList<string>> ListSuggestedRecipientsAsync(DotMarcDbContext context, int groupId, CancellationToken cancellationToken = default) =>
        (await context.UserAccesses.AsNoTracking()
            .Where(access => access.ScopedGroups.Any(group => group.Id == groupId))
            .Select(access => access.Email)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        .Select(email => email.ToLowerInvariant())
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>The report the schedule will send next and when: the last complete period, unless it has been dealt with
    /// (sent, failed or skipped) or fell due before the schedule started, in which case the one after it.</summary>
    public static async Task<(ReportPeriod Period, DateTimeOffset DueUtc)> GetNextScheduledAsync(DotMarcDbContext context, GroupReportSchedule schedule, TimeZoneInfo zone, int sendHour, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var next = ReportPeriods.Previous(schedule.Frequency, zone, nowUtc);
        var handled = ReportPeriods.DueUtc(next, zone, sendHour) < schedule.StartedUtc
            || await context.ClientReportDeliveries.AnyAsync(delivery => delivery.GroupId == schedule.GroupId && delivery.Kind == ClientReportDeliveryKind.Scheduled
                && delivery.PeriodStart == next.Start && delivery.PeriodEnd == next.End && delivery.Status != ClientReportDeliveryStatus.Pending, cancellationToken).ConfigureAwait(false);
        if (handled)
        {
            next = next.Kind switch
            {
                ReportPeriodKind.Week => ReportPeriods.Week(next.End.AddDays(1)),
                ReportPeriodKind.Month => ReportPeriods.Month(next.End.AddDays(1)),
                _ => ReportPeriods.Quarter(next.End.AddDays(1)),
            };
        }

        return (next, ReportPeriods.DueUtc(next, zone, sendHour));
    }

    public static async Task<IReadOnlyList<ClientReportDelivery>> ListDeliveriesAsync(DotMarcDbContext context, int groupId, int count, CancellationToken cancellationToken = default) =>
        await context.ClientReportDeliveries.AsNoTracking()
            .Where(delivery => delivery.GroupId == groupId)
            .OrderByDescending(delivery => delivery.LastAttemptUtc ?? delivery.SentUtc)
            .ThenByDescending(delivery => delivery.Id)
            .Take(count)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}
