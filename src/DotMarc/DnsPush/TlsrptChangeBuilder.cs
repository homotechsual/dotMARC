using DotMarc.Graph;
using Microsoft.Extensions.Options;

namespace DotMarc.DnsPush;

public sealed class TlsrptChangeBuilder(ITlsrptTxtLookup tlsrptTxtLookup, IOptions<GraphOptions> graphOptions) : IDnsChangeBuilder
{
    public string Target => "tlsrpt";
    public string RequiredPolicy => "DomainsEdit";
    public bool WritesToDomainZone => true;

    public async Task<DnsChangePlan> BuildAsync(DnsPushRequest request, CancellationToken cancellationToken)
    {
        var mailbox = graphOptions.Value.TlsrptMailboxAddress;
        if (string.IsNullOrWhiteSpace(mailbox))
        {
            return DnsChangePlan.Refuse("error");
        }

        var domainName = request.Domain.Name;
        var existing = await tlsrptTxtLookup.LookupAsync(domainName, cancellationToken).ConfigureAwait(false);
        if (existing.DelegatedToCname is not null)
        {
            return DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Replace, "TXT", $"_smtp._tls.{domainName}", $"v=TLSRPTv1; rua=mailto:{mailbox}", existing.DelegatedToCname, request.DomainZone!, ExistingRecordType: "CNAME"));
        }

        if (existing.DirectValue is null)
        {
            return DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"_smtp._tls.{domainName}", $"v=TLSRPTv1; rua=mailto:{mailbox}", null, request.DomainZone!));
        }

        var merged = TlsrptRuaMerge.TryMerge(existing.DirectValue, mailbox);
        return merged is null
            ? DnsChangePlan.Refuse("unmergeable")
            : DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"_smtp._tls.{domainName}", merged, existing.DirectValue, request.DomainZone!));
    }
}
