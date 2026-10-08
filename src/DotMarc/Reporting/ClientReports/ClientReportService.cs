using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Reporting.ClientReports;

/// <summary>A Group's report schedule and delivery history.</summary>
public static class ClientReportService
{
    public static Task<GroupReportSchedule?> GetScheduleAsync(DotMarcDbContext context, int groupId, CancellationToken cancellationToken = default) =>
        context.GroupReportSchedules.AsNoTracking().SingleOrDefaultAsync(schedule => schedule.GroupId == groupId, cancellationToken);

    public static async Task SetScheduleAsync(DotMarcDbContext context, AuditActor actor, int groupId, ReportFrequency frequency, IReadOnlyList<string> recipients, CancellationToken cancellationToken = default)
    {
        var tidied = ReportRecipients.Normalise(recipients);
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

    public static async Task<IReadOnlyList<ClientReportDelivery>> ListDeliveriesAsync(DotMarcDbContext context, int groupId, int count, CancellationToken cancellationToken = default) =>
        await context.ClientReportDeliveries.AsNoTracking()
            .Where(delivery => delivery.GroupId == groupId)
            .OrderByDescending(delivery => delivery.LastAttemptUtc ?? delivery.SentUtc)
            .ThenByDescending(delivery => delivery.Id)
            .Take(count)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}
