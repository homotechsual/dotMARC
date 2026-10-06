using DotMarc.Audit;
using DotMarc.Dns;
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
    public static async Task<AddDomainResult> AddDomainAsync(DotMarcDbContext context, AuditActor actor, string rawName, CancellationToken cancellationToken = default)
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
        var domain = new Domain { Name = normalized, FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = true, SortOrder = nextSortOrder };
        context.Domains.Add(domain);

        try
        {
            await AuditLog.SaveAndRecordAsync(context,
                () => AuditLog.Record(context, actor, AuditActions.DomainAdded, AuditTarget.For(domain), $"Added domain {domain.Name}"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // The unique index on Domain.Name (DotMarcDbContext.cs) caught a race: another request
            // inserted the same domain between our AnyAsync check and this save. Same outcome as
            // the pre-check catching it, just reported the same way to the caller. Only the
            // unique-violation SQL state ("23505") is treated this way - any other DbUpdateException
            // (connection drop, disk full, permission failure) propagates instead of being
            // misreported as "already monitored", which would point the caller at the wrong problem.
            return AddDomainResult.AlreadyMonitored;
        }

        return AddDomainResult.Added;
    }

    /// <summary>Permanently deletes a Domain row. DotMarcDbContext.cs configures cascade delete
    /// from Domain to Report and Report to ReportRecord, so this also removes all report history
    /// for the domain - callers (ManageDomains.razor) confirm that with the user first when the
    /// domain has any reports.</summary>
    public static async Task RemoveDomainAsync(DotMarcDbContext context, AuditActor actor, int domainId, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        AuditLog.Record(context, actor, AuditActions.DomainRemoved, AuditTarget.For(domain), $"Removed domain {domain.Name}");
        context.Domains.Remove(domain);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task SetMonitoredAsync(DotMarcDbContext context, AuditActor actor, int domainId, bool isMonitored, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Field("Monitored", domain.IsMonitored, isMonitored);
        if (!changes.Any)
        {
            return;
        }

        domain.IsMonitored = isMonitored;
        AuditLog.Record(context, actor, AuditActions.DomainMonitoringChanged, AuditTarget.For(domain),
            $"{(isMonitored ? "Started" : "Stopped")} monitoring {domain.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets (or clears, with null) a domain's Halo client override, from Manage Domains.</summary>
    public static async Task SetHaloClientIdAsync(DotMarcDbContext context, AuditActor actor, int domainId, int? haloClientId, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Field("Halo client", domain.HaloClientId, haloClientId);
        if (!changes.Any)
        {
            return;
        }

        domain.HaloClientId = haloClientId;
        AuditLog.Record(context, actor, AuditActions.DomainHaloClientChanged, AuditTarget.For(domain), $"Changed the Halo client for {domain.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Saves a domain's MTA-STS hosting configuration from the domain detail page's MTA-STS
    /// tab. Enabling hosting for the first time (false -&gt; true) resets MtaStsStatus to PendingDns
    /// so PollingService's MTA-STS cycle picks it up on its next ~15 minute pass; disabling it
    /// (true -&gt; false) is intentionally left alone here - that same cycle detects the flip and
    /// runs IMtaStsHostProvisioner.TeardownAsync before resetting the status itself, since teardown
    /// is a network call this pure-DB service does not make.</summary>
    public static async Task SetMtaStsConfigAsync(
        DotMarcDbContext context,
        AuditActor actor,
        int domainId,
        bool enabled,
        MtaStsMode mode,
        List<string> mxHosts,
        int maxAgeSeconds,
        CancellationToken cancellationToken = default)
    {
        // The served policy lists exactly these hosts; with none, senders that honour MTA-STS have nowhere they may deliver.
        if (enabled && mxHosts.Count == 0)
        {
            throw new ArgumentException("MTA-STS can't be on without MX hosts. Look them up from DNS or add them first.", nameof(mxHosts));
        }

        var domain = await context.Domains.SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges()
            .Field("Hosting enabled", domain.MtaStsEnabled, enabled)
            .Field("Mode", domain.MtaStsMode, mode)
            .Set("MX hosts", domain.MtaStsMxHosts, mxHosts)
            .Field("Max age (seconds)", domain.MtaStsMaxAgeSeconds, maxAgeSeconds);
        if (!changes.Any)
        {
            return;
        }

        if (enabled && !domain.MtaStsEnabled)
        {
            domain.MtaStsStatus = MtaStsStatus.PendingDns;
            domain.MtaStsCheckDetail = null;
            domain.MtaStsCheckedUtc = null;
        }

        domain.MtaStsEnabled = enabled;
        domain.MtaStsMode = mode;
        domain.MtaStsMxHosts = mxHosts;
        domain.MtaStsMaxAgeSeconds = maxAgeSeconds;

        AuditLog.Record(context, actor, AuditActions.DomainMtaStsChanged, AuditTarget.For(domain), $"Changed MTA-STS for {domain.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Saves a domain's DKIM selector list from the domain detail page's "Configure DKIM
    /// selectors" dialog. Does not itself trigger a recheck - the dialog's own save handler does
    /// that immediately afterward via PollingService.RunSingleDkimCheckAsync, matching the "enable
    /// MTA-STS" flow's immediate-check-after-save pattern.</summary>
    public static async Task SetDkimSelectorsAsync(DotMarcDbContext context, AuditActor actor, int domainId, List<string> selectors, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.Include(d => d.DkimRecords).SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Set("DKIM selectors", domain.DkimSelectors, selectors);
        if (!changes.Any)
        {
            return;
        }

        // A removed selector's stored record goes with it.
        foreach (var orphan in domain.DkimRecords.Where(record => !selectors.Contains(record.Selector, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            changes.Field($"DKIM {orphan.Selector}", DkimRecordValue.Describe(orphan), (string?)null);
            domain.DkimRecords.Remove(orphan);
        }

        domain.DkimSelectors = selectors;
        AuditLog.Record(context, actor, AuditActions.DomainDkimSelectorsChanged, AuditTarget.For(domain), $"Changed the DKIM selectors for {domain.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets the stored DKIM record for each of the domain's selectors: a blank value removes it, and inputs
    /// for selectors the domain doesn't have are ignored. Values are tidied, then checked; an invalid one throws an
    /// ArgumentException naming its selector, and nothing is saved.</summary>
    public static async Task SetDkimRecordsAsync(DotMarcDbContext context, AuditActor actor, int domainId, IReadOnlyList<DkimRecordInput> records, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.Include(d => d.DkimRecords).SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges();
        foreach (var selector in domain.DkimSelectors)
        {
            var input = records.FirstOrDefault(candidate => string.Equals(candidate.Selector, selector, StringComparison.OrdinalIgnoreCase));
            var existing = domain.DkimRecords.FirstOrDefault(record => string.Equals(record.Selector, selector, StringComparison.OrdinalIgnoreCase));
            var before = existing is null ? null : DkimRecordValue.Describe(existing);

            if (input is null || string.IsNullOrWhiteSpace(input.Value))
            {
                if (existing is not null)
                {
                    domain.DkimRecords.Remove(existing);
                    changes.Field($"DKIM {selector}", before, (string?)null);
                }

                continue;
            }

            var value = DkimRecordValue.Normalize(input.Type, input.Value);
            if (DkimRecordValue.Validate(input.Type, value) is { } problem)
            {
                throw new ArgumentException($"{selector}: {problem}");
            }

            if (existing is null)
            {
                existing = new DomainDkimRecord { Selector = selector, RecordType = input.Type, Value = value };
                domain.DkimRecords.Add(existing);
            }
            else
            {
                existing.RecordType = input.Type;
                existing.Value = value;
            }

            changes.Field($"DKIM {selector}", before, DkimRecordValue.Describe(existing));
        }

        if (!changes.Any)
        {
            return;
        }

        AuditLog.Record(context, actor, AuditActions.DomainDkimRecordsChanged, AuditTarget.For(domain), $"Changed the DKIM records for {domain.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Persists a full custom display order: SortOrder is set to each domain's index in
    /// orderedDomainIds. A full-list resequence rather than a gap/fractional scheme - simple, and
    /// correct at the scale (a handful to a few dozen domains) this app is designed for. Two
    /// domains can end up with the same SortOrder if a manual add (AddDomainAsync) races a
    /// report-driven one (PollingService.StoreReportAsync) - there's no uniqueness constraint on
    /// the column, and every ordering query breaks such ties with .ThenBy(d => d.Name), so this is
    /// tolerated by design rather than guarded against.</summary>
    public static async Task ReorderAsync(DotMarcDbContext context, AuditActor actor, IReadOnlyList<int> orderedDomainIds, CancellationToken cancellationToken = default)
    {
        var domains = await context.Domains
            .Where(d => orderedDomainIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, cancellationToken)
            .ConfigureAwait(false);

        var orderBefore = domains.Values.OrderBy(domain => domain.SortOrder).ThenBy(domain => domain.Name).Select(domain => domain.Name).ToList();

        for (var index = 0; index < orderedDomainIds.Count; index++)
        {
            // A domain deleted concurrently (between the caller building this list and this call)
            // is simply skipped rather than throwing - the caller's next reload drops it from the
            // displayed list anyway, so there's nothing left to assign an order to.
            if (domains.TryGetValue(orderedDomainIds[index], out var domain))
            {
                domain.SortOrder = index;
            }
        }

        var orderAfter = orderedDomainIds.Where(domains.ContainsKey).Select(domainId => domains[domainId].Name).ToList();
        var changes = new AuditChanges().Field("Order", string.Join(", ", orderBefore), string.Join(", ", orderAfter));
        if (changes.Any)
        {
            AuditLog.Record(context, actor, AuditActions.DomainsReordered, null, "Reordered domains", changes);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
