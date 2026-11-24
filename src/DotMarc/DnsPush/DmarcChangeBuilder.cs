using DotMarc.Baselines;
using DotMarc.Dns;
using DotMarc.Graph;
using DotMarc.Notifications;
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
        var newPolicy = target is null ? "p=none" : DmarcTargetText.Policy(target);
        var newRecord = $"v=DMARC1; {newPolicy}; {ruaTag}";
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

        var txtValues = existing.TxtValues.Count > 0 ? existing.TxtValues : [existing.DirectValue];
        var analysis = DmarcRecordAnalysis.Analyse(txtValues);

        // Only these values are DMARC and may be removed; anything else at the name (a verification value, say) is left alone.
        var dmarcValues = analysis.Records.Concat(analysis.IgnoredCandidates).ToList();
        if (dmarcValues.Count == 0)
        {
            // Nothing at the name reads as DMARC, so there is nothing to repair; merging into the value refuses it.
            return MergePlan(request, existing.DirectValue, existing.DirectValue, settings, []);
        }

        var repair = DmarcRecordRepair.Build(analysis, target);
        var notes = new List<string>();
        if (!analysis.IsValid)
        {
            notes.Add("Repairs a malformed DMARC record");
        }

        notes.AddRange(repair?.Changes ?? []);
        notes.AddRange(analysis.IgnoredCandidates.Select(value => $"Removes a value that looked like a DMARC record but was malformed: {value}"));

        var repaired = repair?.Record ?? analysis.Records[0];
        if (dmarcValues.Count == 1)
        {
            return MergePlan(request, repaired, dmarcValues[0], settings, notes);
        }

        var merged = DmarcRuaMerge.TryMerge(repaired, graphOptions.Value.MailboxAddress, settings.AdditionalRuaAddress, settings.KeepOtherRuaAddresses);
        return merged is null
            ? DnsChangePlan.Refuse("unmergeable")
            : new DnsChangePlan([new DnsRecordChange(DnsRecordChangeKind.ReplaceTxtValues, "TXT", $"_dmarc.{domainName}", merged, string.Join(" | ", dmarcValues), request.DomainZone!, ValuesToRemove: dmarcValues)], null) { Notes = notes };
    }

    private DnsChangePlan MergePlan(DnsPushRequest request, string record, string existingValue, DnsRecordSettings settings, List<string> notes)
    {
        var merged = DmarcRuaMerge.TryMerge(record, graphOptions.Value.MailboxAddress, settings.AdditionalRuaAddress, settings.KeepOtherRuaAddresses);
        return merged is null
            ? DnsChangePlan.Refuse("unmergeable")
            : new DnsChangePlan([new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"_dmarc.{request.Domain.Name}", merged, existingValue, request.DomainZone!)], null) { Notes = notes };
    }
}
