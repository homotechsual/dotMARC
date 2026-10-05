using DotMarc.Data;
using DotMarc.Dns;

namespace DotMarc.DnsPush;

/// <summary>Publishes each stored DKIM record that DNS doesn't already match: create when nothing is there, update
/// when the same type has a different value, replace when the other type is there.</summary>
public sealed class DkimChangeBuilder(ITxtRecordLookup txtLookup) : IDnsChangeBuilder
{
    public string Target => "dkim";
    public string RequiredPolicy => "DomainsEdit";
    public bool WritesToDomainZone => true;

    public async Task<DnsChangePlan> BuildAsync(DnsPushRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await BuildFromDnsAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                          && !cancellationToken.IsCancellationRequested)
        {
            // A DNS lookup failed: the popup says the push failed rather than showing an error page.
            return DnsChangePlan.Refuse("error");
        }
    }

    private async Task<DnsChangePlan> BuildFromDnsAsync(DnsPushRequest request, CancellationToken cancellationToken)
    {
        var domain = request.Domain;
        var changes = new List<DnsRecordChange>();
        foreach (var record in domain.DkimRecords.Where(record => domain.DkimSelectors.Contains(record.Selector, StringComparer.OrdinalIgnoreCase)))
        {
            var name = $"{record.Selector}._domainkey.{domain.Name}";
            var live = await txtLookup.LookupWithCnameAsync(name, cancellationToken).ConfigureAwait(false);
            var cname = live.DelegatedToCname?.TrimEnd('.');
            var zone = request.DomainZone!;

            if (record.RecordType == DkimRecordType.Cname)
            {
                if (string.Equals(cname, record.Value, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                changes.Add(cname is not null
                    ? new DnsRecordChange(DnsRecordChangeKind.Merge, "CNAME", name, record.Value, cname, zone)
                    : live.DirectValue is not null
                        ? new DnsRecordChange(DnsRecordChangeKind.Replace, "CNAME", name, record.Value, live.DirectValue, zone, ExistingRecordType: "TXT")
                        : new DnsRecordChange(DnsRecordChangeKind.Create, "CNAME", name, record.Value, null, zone));
            }
            else if (cname is not null)
            {
                changes.Add(new DnsRecordChange(DnsRecordChangeKind.Replace, "TXT", name, record.Value, cname, zone, ExistingRecordType: "CNAME"));
            }
            else if (live.DirectValue is null)
            {
                changes.Add(new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", name, record.Value, null, zone));
            }
            else if (DkimRecordValue.PublicKey(live.DirectValue) != DkimRecordValue.PublicKey(record.Value))
            {
                changes.Add(new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", name, record.Value, live.DirectValue, zone));
            }
        }

        return changes.Count == 0 ? DnsChangePlan.Refuse("nothing-to-push") : new DnsChangePlan(changes, null);
    }
}
