using DotMarc.Graph;
using Microsoft.Extensions.Options;

namespace DotMarc.DnsPush;

/// <summary>One reviewed provider authorization for a domain's DMARC, TLS reporting and enabled MTA-STS hosting records.</summary>
public sealed class DomainSetupChangeBuilder(DmarcChangeBuilder dmarc, TlsrptChangeBuilder tlsrpt, MtaStsChangeBuilder mtaSts, IOptions<GraphOptions> graph) : IDnsChangeBuilder
{
    public string Target => "domain-setup";
    public string RequiredPolicy => "DomainDnsSetup";
    public bool WritesToDomainZone => true;

    public async Task<DnsChangePlan> BuildAsync(DnsPushRequest request, CancellationToken cancellationToken)
    {
        var changes = new List<DnsRecordChange>();
        var notes = new List<string> { "Sets up DMARC and TLS reporting together. SPF, DKIM keys and external report authorization require separate review." };
        var builders = new List<IDnsChangeBuilder> { dmarc };
        if (!string.IsNullOrWhiteSpace(graph.Value.TlsrptMailboxAddress)) builders.Add(tlsrpt);
        else notes.Add("TLS reporting is not included because this instance has no configured TLS report mailbox.");
        if (request.Domain.MtaStsEnabled)
        {
            builders.Add(mtaSts);
            notes.Add("After hosting and HTTPS provisioning complete, publish the _mta-sts policy ID from the MTA-STS controls.");
        }
        foreach (var builder in builders)
        {
            var plan = await builder.BuildAsync(request, cancellationToken).ConfigureAwait(false);
            if (plan.Refusal is not null) return DnsChangePlan.Refuse(plan.Refusal);
            changes.AddRange(plan.Changes);
            notes.AddRange(plan.Notes);
        }
        notes.Add("Provider writes are not transactional. If a later record fails, earlier changes may already be published; review current DNS before retrying.");
        if (request.Payload is not null && request.Payload != DnsPlanFingerprint.Of(changes))
            return DnsChangePlan.Refuse("setup-changed");
        return new DnsChangePlan(changes, null) { Notes = notes };
    }
}
