using DotMarc.Graph;
using Microsoft.Extensions.Options;

namespace DotMarc.DnsPush;

public sealed class DmarcChangeBuilder(IDmarcTxtLookup dmarcTxtLookup, IOptions<GraphOptions> graphOptions, IDnsRecordSettingsReader settingsReader) : IDnsChangeBuilder
{
    public string Target => "dmarc";
    public string RequiredPolicy => "DomainsEdit";
    public bool WritesToDomainZone => true;

    public async Task<DnsChangePlan> BuildAsync(DnsPushRequest request, CancellationToken cancellationToken)
    {
        var domainName = request.Domain.Name;
        var existing = await dmarcTxtLookup.LookupAsync(domainName, cancellationToken).ConfigureAwait(false);
        var mailbox = graphOptions.Value.MailboxAddress;
        var settings = await settingsReader.GetAsync(cancellationToken).ConfigureAwait(false);
        var ruaTag = DmarcRuaMerge.RuaTag(mailbox, settings.AdditionalRuaAddress);
        if (existing.DelegatedToCname is not null)
        {
            // The record is a CNAME delegated to a third party - DNS doesn't allow a CNAME to
            // coexist with any other record type at the same name, so there's no in-place merge
            // here, only delete-then-create. The confirm dialog makes this explicit before the
            // user ever reaches this endpoint (DnsRecordPushDecision.NeedsConfirmation always
            // returns true when DelegatedToCname is set).
            return DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Replace, "TXT", $"_dmarc.{domainName}", $"v=DMARC1; p=none; {ruaTag}", existing.DelegatedToCname, request.DomainZone!, ExistingRecordType: "CNAME"));
        }

        if (existing.DirectValue is null)
        {
            return DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"_dmarc.{domainName}", $"v=DMARC1; p=none; {ruaTag}", null, request.DomainZone!));
        }

        var merged = DmarcRuaMerge.TryMerge(existing.DirectValue, mailbox, settings.AdditionalRuaAddress, settings.KeepOtherRuaAddresses);
        return merged is null
            ? DnsChangePlan.Refuse("unmergeable")
            : DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"_dmarc.{domainName}", merged, existing.DirectValue, request.DomainZone!));
    }
}
