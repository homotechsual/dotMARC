using DotMarc.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DotMarc.IpEnrichment;

/// <summary>Caches RDAP lookup results in IpInfo, keyed by IP. Follows this codebase's convention
/// of a static class operating on a caller-supplied DotMarcDbContext (see AccessBootstrapper,
/// DomainManagementService) for the read path, and its own short-lived context per write for the
/// background-enrichment path (see DomainDetail.razor, Task 4) since that write happens well
/// after the page's own context has already been disposed.</summary>
public static class IpInfoService
{
    /// <summary>How long a NotFound/LookupFailed result is trusted before being retried. An Ok
    /// result has no expiry — IP block ownership changes rarely enough that re-querying on a
    /// schedule isn't worth it, unlike the DMARC DNS check, which re-checks every 24h because DNS
    /// records genuinely do change often.</summary>
    public static readonly TimeSpan FailureRetryWindow = TimeSpan.FromHours(24);

    public static async Task<Dictionary<string, IpInfo>> GetCachedAsync(DotMarcDbContext context, IReadOnlyList<string> ips, CancellationToken cancellationToken) =>
        await context.IpInfos
            .Where(i => ips.Contains(i.Ip))
            .ToDictionaryAsync(i => i.Ip, cancellationToken)
            .ConfigureAwait(false);

    public static bool NeedsLookup(IpInfo? cached, DateTimeOffset nowUtc) =>
        cached is null || (cached.Status != IpLookupStatus.Ok && cached.LookedUpUtc < nowUtc - FailureRetryWindow);

    /// <summary>Looks up one IP and upserts its IpInfo row. Uses its own short-lived context from
    /// dbFactory rather than a caller-supplied one, since the only caller (DomainDetail.razor's
    /// background enrichment) needs to write well after the request that triggered it has already
    /// finished and disposed its own context.</summary>
    public static async Task<IpInfo> EnrichAsync(IDbContextFactory<DotMarcDbContext> dbFactory, IIpInfoLookup lookup, string ip, CancellationToken cancellationToken)
    {
        var result = await lookup.LookupAsync(ip, cancellationToken).ConfigureAwait(false);

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await context.IpInfos.SingleOrDefaultAsync(i => i.Ip == ip, cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            existing = new IpInfo { Ip = ip };
            context.IpInfos.Add(existing);
        }

        existing.Organization = result.Organization;
        existing.Country = result.Country;
        existing.Status = result.Status;
        existing.LookedUpUtc = DateTimeOffset.UtcNow;

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Another concurrent lookup for the same IP (a different domain's Sources tab, or a
            // different visitor, viewed at the same moment) already inserted this row first —
            // matches this codebase's existing race-handling convention (see
            // PollingService.RecordProcessedMessageAsync). Reload its result rather than throwing.
            context.ChangeTracker.Clear();
            existing = await context.IpInfos.SingleAsync(i => i.Ip == ip, cancellationToken).ConfigureAwait(false);
        }

        return existing;
    }
}
