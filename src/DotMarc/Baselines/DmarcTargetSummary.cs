using System.Security.Claims;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Portal;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Baselines;

public sealed record GroupTargetRow(int Id, string Name, DmarcTarget? Target, int DomainCount);

public sealed record DomainOverrideRow(int Id, string Name, DmarcTarget Target);

/// <summary>What the Baselines Domain tab shows: the targets at each level a person can see, and how many of their monitored domains
/// are below target or need checking. A person limited to some groups sees only those groups, the domains in them, and counts over
/// those domains. The counts use monitored domains only, so they match the Dashboard filter they link to.</summary>
public sealed record DmarcTargetSummary(
    DmarcTarget? System,
    IReadOnlyList<GroupTargetRow> Groups,
    IReadOnlyList<DomainOverrideRow> Overrides,
    int BelowCount,
    int NeedsCheckingCount)
{
    public static async Task<DmarcTargetSummary> LoadAsync(DotMarcDbContext context, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var scopedGroupIds = PortalData.ScopedGroupIds(user).ToList();
        var isLimited = scopedGroupIds.Count > 0;

        var settings = await DnsRecordSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);

        var groupRows = await context.Groups
            .AsNoTracking()
            .Where(group => !isLimited || scopedGroupIds.Contains(group.Id))
            .OrderBy(group => group.Name)
            .Select(group => new { group.Id, group.Name, group.TargetDmarcPolicy, group.TargetDmarcSubdomainPolicy, DomainCount = group.Domains.Count })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var groups = groupRows
            .Select(row => new GroupTargetRow(
                row.Id,
                row.Name,
                row.TargetDmarcPolicy is { } policy ? new DmarcTarget(policy, row.TargetDmarcSubdomainPolicy) : null,
                row.DomainCount))
            .ToList();

        var visibleDomains = await context.Domains
            .AsNoTracking()
            .Include(domain => domain.Groups)
            .Where(domain => !isLimited || domain.Groups.Any(group => scopedGroupIds.Contains(group.Id)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var overrides = visibleDomains
            .Where(domain => domain.DmarcTarget is not null)
            .OrderBy(domain => domain.Name, StringComparer.OrdinalIgnoreCase)
            .Select(domain => new DomainOverrideRow(domain.Id, domain.Name, domain.DmarcTarget!))
            .ToList();

        var outcomes = visibleDomains
            .Where(domain => domain.IsMonitored)
            .Select(domain => DmarcTargetCheck.Outcome(domain, DmarcTargetResolver.Resolve(domain, settings)))
            .ToList();

        return new DmarcTargetSummary(
            settings.DmarcTarget,
            groups,
            overrides,
            outcomes.Count(outcome => outcome == DmarcTargetOutcome.Below),
            outcomes.Count(outcome => outcome == DmarcTargetOutcome.NeedsChecking));
    }
}
