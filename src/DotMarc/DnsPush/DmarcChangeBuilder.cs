using DotMarc.Baselines;
using DotMarc.Graph;
using Microsoft.Extensions.Options;

namespace DotMarc.DnsPush;

public sealed class DmarcChangeBuilder(IDmarcTxtLookup dmarcTxtLookup, IOptions<GraphOptions> graphOptions, IDnsRecordSettingsReader settingsReader, IDmarcTargetReader targetReader) : IDnsChangeBuilder
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
        var target = await targetReader.GetAsync(request.Domain.Id, cancellationToken).ConfigureAwait(false);
        var newRecord = $"v=DMARC1; {DmarcPolicyRaise.NewRecordPolicy(target)}; {ruaTag}";
        if (existing.DelegatedToCname is not null)
        {
            // The record is a CNAME delegated to a third party - DNS doesn't allow a CNAME to
            // coexist with any other record type at the same name, so there's no in-place merge
            // here, only delete-then-create. The review screen (DnsPushReview) makes this explicit
            // before the user ever reaches this endpoint, and flags the lost delegation.
            return DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Replace, "TXT", $"_dmarc.{domainName}", newRecord, existing.DelegatedToCname, request.DomainZone!, ExistingRecordType: "CNAME"));
        }

        if (existing.DirectValue is null)
        {
            return DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"_dmarc.{domainName}", newRecord, null, request.DomainZone!));
        }

        var merged = DmarcRuaMerge.TryMerge(existing.DirectValue, mailbox, settings.AdditionalRuaAddress, settings.KeepOtherRuaAddresses);
        if (merged is not null && target is not null)
        {
            merged = DmarcPolicyRaise.Apply(merged, target);
        }

        return merged is null
            ? DnsChangePlan.Refuse("unmergeable")
            : DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"_dmarc.{domainName}", merged, existing.DirectValue, request.DomainZone!));
    }
}
