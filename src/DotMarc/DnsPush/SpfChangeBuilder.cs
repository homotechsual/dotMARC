using DotMarc.Dns;

namespace DotMarc.DnsPush;

/// <summary>Publishes the SPF editor's record. It re-reads the live record(s) first and refuses if they changed since
/// the editor opened, re-counts the lookups, and replaces only the SPF value(s) among the apex's TXT records.</summary>
public sealed class SpfChangeBuilder(ITxtRecordLookup txtLookup, SpfLookupCounter lookupCounter) : IDnsChangeBuilder
{
    public string Target => "spf";
    public string RequiredPolicy => "DomainsEdit";
    public bool WritesToDomainZone => true;

    public async Task<DnsChangePlan> BuildAsync(DnsPushRequest request, CancellationToken cancellationToken)
    {
        var payload = SpfPushPayload.TryParse(request.Payload);
        if (payload is null || !SpfRecord.IsSpf(payload.Proposed))
        {
            return DnsChangePlan.Refuse("invalid");
        }

        var domainName = request.Domain.Name;
        var live = (await txtLookup.GetTxtValuesAsync(domainName, cancellationToken).ConfigureAwait(false)).Where(SpfRecord.IsSpf).ToList();
        if (SpfEditor.Fingerprint(live) != payload.Fingerprint)
        {
            return DnsChangePlan.Refuse("spf-changed");
        }

        var proposed = SpfRecord.Parse(payload.Proposed);
        var proposedCount = await lookupCounter.CountAsync(domainName, proposed, cancellationToken).ConfigureAwait(false);
        SpfLookupCount? worstLiveCount = null;
        foreach (var liveValue in live)
        {
            var liveCount = await lookupCounter.CountAsync(domainName, SpfRecord.Parse(liveValue), cancellationToken).ConfigureAwait(false);
            if (worstLiveCount is null || liveCount.Total > worstLiveCount.Total)
            {
                worstLiveCount = liveCount;
            }
        }

        if (SpfEditor.BlockReason(proposed.Format(), proposedCount, live, worstLiveCount) is { } reason)
        {
            return DnsChangePlan.Refuse(reason);
        }

        return DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.ReplaceTxtValues, "TXT", domainName, proposed.Format(),
            live.Count == 0 ? null : string.Join(" | ", live), request.DomainZone!, ValuesToRemove: live));
    }
}
