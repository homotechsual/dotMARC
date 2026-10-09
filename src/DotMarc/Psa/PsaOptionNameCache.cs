using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Psa.Autotask;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Psa;

/// <summary>Stores the names of each PSA's saved choices (ticket type, priority, closed status and so on) as soon as
/// its options are loaded, not only on save. Otherwise a choice saved before names were kept, or options loaded but not
/// saved, shows as a bare number again after a reload. Only the saved choices' names change, never a choice itself, so
/// this isn't a settings change and isn't audited.</summary>
public static class PsaOptionNameCache
{
    public static async Task RememberHaloAsync(DotMarcDbContext context, IEnumerable<HaloTicketType> ticketTypes, IEnumerable<HaloPriority> priorities,
        IEnumerable<HaloTicketStatus> statuses, IEnumerable<HaloAgent> agents, CancellationToken cancellationToken = default)
    {
        var saved = await context.HaloPsaSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        HaloOptionNames.Remember(saved, ticketTypes, priorities, statuses, agents);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The board's statuses and types belong to the saved board, so pass the lists for that board.</summary>
    public static async Task RememberConnectWiseAsync(DotMarcDbContext context, IEnumerable<PsaOption> boards, IEnumerable<PsaOption> statuses,
        IEnumerable<PsaOption> types, IEnumerable<PsaOption> priorities, CancellationToken cancellationToken = default)
    {
        var saved = await context.ConnectWiseSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        saved.BoardName = NameOf(boards, saved.BoardId) ?? saved.BoardName;
        saved.StatusName = NameOf(statuses, saved.StatusId) ?? saved.StatusName;
        saved.TypeName = NameOf(types, saved.TypeId) ?? saved.TypeName;
        saved.PriorityName = NameOf(priorities, saved.PriorityId) ?? saved.PriorityName;
        saved.ClosedStatusName = NameOf(statuses, saved.ClosedStatusId) ?? saved.ClosedStatusName;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task RememberAutotaskAsync(DotMarcDbContext context, AutotaskTicketPicklists picklists, CancellationToken cancellationToken = default)
    {
        var saved = await context.AutotaskSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        saved.QueueName = NameOf(picklists.Queues, saved.QueueId) ?? saved.QueueName;
        saved.TicketTypeName = NameOf(picklists.TicketTypes, saved.TicketTypeId) ?? saved.TicketTypeName;
        saved.IssueTypeName = NameOf(picklists.IssueTypes, saved.IssueTypeId) ?? saved.IssueTypeName;
        saved.PriorityName = NameOf(picklists.Priorities, saved.PriorityId) ?? saved.PriorityName;
        saved.ClosedStatusName = NameOf(picklists.Statuses, saved.ClosedStatusId) ?? saved.ClosedStatusName;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? NameOf(IEnumerable<PsaOption> options, int? id) =>
        id is { } selected ? options.FirstOrDefault(option => option.Id == selected)?.Name : null;
}
