using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Baselines;

/// <summary>Saves the target DMARC policy at system, group and domain level, with an audit entry when it changes.</summary>
public static class DmarcTargetService
{
    public static async Task SetSystemAsync(DotMarcDbContext context, AuditActor actor, DmarcTarget? target, CancellationToken cancellationToken = default)
    {
        var settings = await context.DnsRecordSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        await ApplyAsync(context, actor, settings.DmarcTarget, target, AuditTarget.Settings("Baselines"),
            (policy, subdomainPolicy) => { settings.TargetDmarcPolicy = policy; settings.TargetDmarcSubdomainPolicy = subdomainPolicy; },
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> SetGroupAsync(DotMarcDbContext context, AuditActor actor, int groupId, DmarcTarget? target, CancellationToken cancellationToken = default)
    {
        var group = await context.Groups.SingleOrDefaultAsync(candidate => candidate.Id == groupId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return false;
        }

        await ApplyAsync(context, actor, group.DmarcTarget, target, AuditTarget.For(group),
            (policy, subdomainPolicy) => { group.TargetDmarcPolicy = policy; group.TargetDmarcSubdomainPolicy = subdomainPolicy; },
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    public static async Task<bool> SetDomainAsync(DotMarcDbContext context, AuditActor actor, int domainId, DmarcTarget? target, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.SingleOrDefaultAsync(candidate => candidate.Id == domainId, cancellationToken).ConfigureAwait(false);
        if (domain is null)
        {
            return false;
        }

        await ApplyAsync(context, actor, domain.DmarcTarget, target, AuditTarget.For(domain),
            (policy, subdomainPolicy) => { domain.TargetDmarcPolicy = policy; domain.TargetDmarcSubdomainPolicy = subdomainPolicy; },
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task ApplyAsync(DotMarcDbContext context, AuditActor actor, DmarcTarget? current, DmarcTarget? requested,
        AuditTarget auditTarget, Action<DmarcPolicyLevel?, DmarcPolicyLevel?> store, CancellationToken cancellationToken)
    {
        // A subdomain policy equal to the policy is the same as none: it follows p.
        var normalised = requested is null
            ? null
            : requested with { SubdomainPolicy = requested.SubdomainPolicy == requested.Policy ? null : requested.SubdomainPolicy };
        var changes = new AuditChanges().Field("DMARC target", DmarcTargetText.Stored(current), DmarcTargetText.Stored(normalised));
        if (!changes.Any)
        {
            return;
        }

        store(normalised?.Policy, normalised?.SubdomainPolicy);
        AuditLog.Record(context, actor, AuditActions.DmarcTargetSaved, auditTarget, "Saved DMARC target", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
