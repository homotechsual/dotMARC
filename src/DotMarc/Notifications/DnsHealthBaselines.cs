using DotMarc.Data;
using DotMarc.Dns;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public static class DnsHealthBaselines
{
    /// <summary>When a DMARC policy weakened or nameservers changed alert is closed by a person (Acknowledge, or
    /// closing its Halo ticket) or by auto-close, the change is accepted: the domain's current policy or nameservers
    /// become the baseline and any pending change is cleared, so the alert doesn't come straight back. Does nothing for
    /// other alert types. Doesn't save; the caller does.</summary>
    public static async Task AcceptCurrentAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken)
    {
        var item = alert.AlertType switch
        {
            AlertTypes.DmarcPolicyWeakened => DnsHealthItems.DmarcPolicy,
            AlertTypes.NameserversChanged => DnsHealthItems.Nameservers,
            _ => null
        };
        if (item is null)
        {
            return;
        }

        var domain = await context.Domains.SingleOrDefaultAsync(candidate => candidate.Name == alert.DomainName, cancellationToken).ConfigureAwait(false);
        if (domain is null)
        {
            return;
        }

        var state = await context.DomainAlertStates
            .SingleOrDefaultAsync(candidate => candidate.DomainId == domain.Id && candidate.Item == item, cancellationToken)
            .ConfigureAwait(false);
        if (state is null)
        {
            state = new DomainAlertState { DomainId = domain.Id, Item = item };
            context.DomainAlertStates.Add(state);
        }

        var current = item == DnsHealthItems.DmarcPolicy
            ? DmarcPolicyTags.Of(domain)?.Format()
            : DnsHealthAlertEvaluator.NameserverKey(domain.DnsNameservers);
        state.Baseline = current ?? state.Baseline;
        state.PendingSinceUtc = null;
        state.RecheckDueUtc = null;
    }
}
