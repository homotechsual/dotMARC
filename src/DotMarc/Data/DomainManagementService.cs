using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DotMarc.Data;

/// <summary>Add/remove/monitor operations for Domain rows created through the "Manage domains" page,
/// as opposed to auto-discovery from an incoming report (see PollingService.StoreReportAsync).
/// Follows this project's DatabaseMigrator/PollingService convention of a static class operating
/// directly on a caller-supplied DotMarcDbContext, rather than owning its own context lifetime.</summary>
public static class DomainManagementService
{
    public enum AddDomainResult { Added, InvalidName, AlreadyMonitored }

    /// <summary>Creates a monitored Domain row with no reports yet, so it immediately shows as
    /// "Missing" on the Dashboard (Dashboard.razor's existing IsMonitored &amp;&amp;
    /// LastReportReceivedUtc-is-null check) until its first real report arrives.</summary>
    public static async Task<AddDomainResult> AddDomainAsync(DotMarcDbContext context, string rawName, CancellationToken cancellationToken = default)
    {
        if (!DomainNameValidator.TryNormalize(rawName, out var normalized))
        {
            return AddDomainResult.InvalidName;
        }

        var exists = await context.Domains.AnyAsync(d => d.Name == normalized, cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return AddDomainResult.AlreadyMonitored;
        }

        var nextSortOrder = (await context.Domains.MaxAsync(d => (int?)d.SortOrder, cancellationToken).ConfigureAwait(false) ?? -1) + 1;
        context.Domains.Add(new Domain { Name = normalized, FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = true, SortOrder = nextSortOrder });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // The unique index on Domain.Name (DotMarcDbContext.cs) caught a race: another request
            // inserted the same domain between our AnyAsync check and this save. Same outcome as
            // the pre-check catching it, just reported the same way to the caller. Only the
            // unique-violation SQL state ("23505") is treated this way — any other DbUpdateException
            // (connection drop, disk full, permission failure) propagates instead of being
            // misreported as "already monitored", which would point the caller at the wrong problem.
            return AddDomainResult.AlreadyMonitored;
        }

        return AddDomainResult.Added;
    }

    /// <summary>Permanently deletes a Domain row. DotMarcDbContext.cs configures cascade delete
    /// from Domain to Report and Report to ReportRecord, so this also removes all report history
    /// for the domain — callers (ManageDomains.razor) confirm that with the user first when the
    /// domain has any reports.</summary>
    public static async Task RemoveDomainAsync(DotMarcDbContext context, int domainId, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        context.Domains.Remove(domain);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task SetMonitoredAsync(DotMarcDbContext context, int domainId, bool isMonitored, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        domain.IsMonitored = isMonitored;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Persists a full custom display order: SortOrder is set to each domain's index in
    /// orderedDomainIds. A full-list resequence rather than a gap/fractional scheme — simple, and
    /// correct at the scale (a handful to a few dozen domains) this app is designed for. Two
    /// domains can end up with the same SortOrder if a manual add (AddDomainAsync) races a
    /// report-driven one (PollingService.StoreReportAsync) — there's no uniqueness constraint on
    /// the column, and every ordering query breaks such ties with .ThenBy(d => d.Name), so this is
    /// tolerated by design rather than guarded against.</summary>
    public static async Task ReorderAsync(DotMarcDbContext context, IReadOnlyList<int> orderedDomainIds, CancellationToken cancellationToken = default)
    {
        var domains = await context.Domains
            .Where(d => orderedDomainIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, cancellationToken)
            .ConfigureAwait(false);

        for (var index = 0; index < orderedDomainIds.Count; index++)
        {
            // A domain deleted concurrently (between the caller building this list and this call)
            // is simply skipped rather than throwing — the caller's next reload drops it from the
            // displayed list anyway, so there's nothing left to assign an order to.
            if (domains.TryGetValue(orderedDomainIds[index], out var domain))
            {
                domain.SortOrder = index;
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
