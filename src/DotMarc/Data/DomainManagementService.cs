using Microsoft.EntityFrameworkCore;

namespace DotMarc.Data;

/// <summary>Add/remove/pin operations for Domain rows created through the "Manage domains" page,
/// as opposed to auto-discovery from an incoming report (see PollingService.StoreReportAsync).
/// Follows this project's DatabaseMigrator/PollingService convention of a static class operating
/// directly on a caller-supplied DotMarcDbContext, rather than owning its own context lifetime.</summary>
public static class DomainManagementService
{
    public enum AddDomainResult { Added, InvalidName, AlreadyMonitored }

    /// <summary>Creates a pinned Domain row with no reports yet, so it immediately shows as
    /// "Missing" on the Dashboard (Dashboard.razor's existing IsPinned &amp;&amp;
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

        context.Domains.Add(new Domain { Name = normalized, FirstSeenUtc = DateTimeOffset.UtcNow, IsPinned = true });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The unique index on Domain.Name (DotMarcDbContext.cs) caught a race: another request
            // inserted the same domain between our AnyAsync check and this save. Same outcome as
            // the pre-check catching it, just reported the same way to the caller.
            return AddDomainResult.AlreadyMonitored;
        }

        return AddDomainResult.Added;
    }
}
