using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.DomainImport;

public sealed record ImportRowOutcome(int LineNumber, string Domain, string Outcome);

public sealed record ImportResult(int Added, int Updated, int Unchanged, int SkippedExisting, int Invalid, int Duplicates, IReadOnlyList<ImportRowOutcome> Rows);

/// <summary>Carries out a confirmed import plan in one transaction, through the existing domain, group and tag services,
/// so every change is audited exactly as if made by hand. Any failure rolls the whole import back.</summary>
public static class DomainImportService
{
    public static async Task<ImportResult> ApplyAsync(DotMarcDbContext context, AuditActor actor, ImportPlan plan, CancellationToken cancellationToken = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // A result other than Added means a group or tag of that name appeared since the preview; it's used below.
        foreach (var groupName in plan.GroupsToCreate)
        {
            await GroupManagementService.AddGroupAsync(context, actor, groupName, cancellationToken).ConfigureAwait(false);
        }

        foreach (var tagName in plan.TagsToCreate)
        {
            await TagManagementService.AddTagAsync(context, actor, tagName, TagManagementService.AllowedColors[0], cancellationToken).ConfigureAwait(false);
        }

        var groupIds = await context.Groups.ToDictionaryAsync(group => group.Name, group => group.Id, StringComparer.OrdinalIgnoreCase, cancellationToken).ConfigureAwait(false);
        var tagIds = await context.Tags.ToDictionaryAsync(tag => tag.Name, tag => tag.Id, StringComparer.OrdinalIgnoreCase, cancellationToken).ConfigureAwait(false);

        var outcomes = new List<ImportRowOutcome>();
        var (added, updated, unchanged, skipped) = (0, 0, 0, 0);
        foreach (var row in plan.Rows)
        {
            var domainLabel = row.Domain ?? row.RawDomain;
            switch (row.Status)
            {
                case ImportRowStatus.Invalid:
                    outcomes.Add(new(row.LineNumber, domainLabel, $"Not imported: {row.InvalidReason}"));
                    continue;
                case ImportRowStatus.Duplicate:
                    outcomes.Add(new(row.LineNumber, domainLabel, $"Merged into line {row.MergedIntoLine}"));
                    continue;
                case ImportRowStatus.AlreadyMonitored when row.Target is null:
                    if (plan.Mode == ExistingDomainMode.Skip)
                    {
                        skipped++;
                        outcomes.Add(new(row.LineNumber, domainLabel, "Already monitored, skipped"));
                    }
                    else
                    {
                        unchanged++;
                        outcomes.Add(new(row.LineNumber, domainLabel, "Already monitored, nothing to change"));
                    }

                    continue;
            }

            int domainId;
            if (row.Status == ImportRowStatus.New)
            {
                var addResult = await DomainManagementService.AddDomainAsync(context, actor, row.Domain!, cancellationToken).ConfigureAwait(false);
                domainId = await context.Domains.Where(domain => domain.Name == row.Domain).Select(domain => domain.Id).SingleAsync(cancellationToken).ConfigureAwait(false);
                if (addResult == DomainManagementService.AddDomainResult.Added)
                {
                    added++;
                    outcomes.Add(new(row.LineNumber, domainLabel, "Added"));
                }
                else if (plan.Mode == ExistingDomainMode.Skip)
                {
                    skipped++;
                    outcomes.Add(new(row.LineNumber, domainLabel, "Added by someone else since the preview, so it was skipped"));
                    continue;
                }
                else
                {
                    updated++;
                    outcomes.Add(new(row.LineNumber, domainLabel, "Already existed by the time of the import, so it was updated"));
                }
            }
            else
            {
                domainId = row.ExistingDomainId!.Value;
                updated++;
                outcomes.Add(new(row.LineNumber, domainLabel, "Updated"));
            }

            await ApplyTargetAsync(context, actor, domainId, row.Target!, groupIds, tagIds, cancellationToken).ConfigureAwait(false);
        }

        var invalid = plan.InvalidCount;
        AuditLog.Record(context, actor, AuditActions.DomainsImported, null,
            $"Imported {added} {(added == 1 ? "domain" : "domains")}, updated {updated}, skipped {invalid} invalid and {skipped} already monitored");
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new ImportResult(added, updated, unchanged, skipped, invalid, plan.DuplicateCount, outcomes);
    }

    private static async Task ApplyTargetAsync(DotMarcDbContext context, AuditActor actor, int domainId, DomainTarget target,
        IReadOnlyDictionary<string, int> groupIds, IReadOnlyDictionary<string, int> tagIds, CancellationToken cancellationToken)
    {
        if (target.Groups is { } groupChange)
        {
            // Worked out against the domain's groups now, not at preview time, so a change made in between isn't undone.
            var current = await context.Domains.Where(domain => domain.Id == domainId).SelectMany(domain => domain.Groups.Select(group => group.Name)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var ids = groupChange.ApplyTo(current).Select(name => groupIds[name]).ToList();
            await GroupManagementService.SetDomainGroupsAsync(context, actor, domainId, ids, cancellationToken).ConfigureAwait(false);
        }

        if (target.Tags is { } tagChange)
        {
            var current = await context.Domains.Where(domain => domain.Id == domainId).SelectMany(domain => domain.Tags.Select(tag => tag.Name)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var ids = tagChange.ApplyTo(current).Select(name => tagIds[name]).ToList();
            await TagManagementService.SetDomainTagsAsync(context, actor, domainId, ids, cancellationToken).ConfigureAwait(false);
        }

        foreach (var (psa, company) in target.PsaCompanies)
        {
            await DomainManagementService.SetPsaCompanyAsync(context, actor, domainId, psa, company, cancellationToken).ConfigureAwait(false);
        }

        if (target.Monitored is { } monitored)
        {
            await DomainManagementService.SetMonitoredAsync(context, actor, domainId, monitored, cancellationToken).ConfigureAwait(false);
        }

        if (target.DkimSelectors is { } selectors)
        {
            await DomainManagementService.SetDkimSelectorsAsync(context, actor, domainId, [.. selectors], cancellationToken).ConfigureAwait(false);
        }

        if (target.MtaSts is { } mtaSts)
        {
            await DomainManagementService.SetMtaStsConfigAsync(context, actor, domainId, mtaSts.Enabled, mtaSts.Mode, [.. mtaSts.MxHosts], mtaSts.MaxAgeSeconds, cancellationToken).ConfigureAwait(false);
        }
    }
}
