using DotMarc.Graph;
using Microsoft.Extensions.Options;

namespace DotMarc.DnsPush;

public sealed class DmarcAuthorizationChangeBuilder(
    IDmarcAuthorizationTxtLookup dmarcAuthorizationTxtLookup, IDnsProviderDetector dnsProviderDetector, IOptions<GraphOptions> graphOptions) : IDnsChangeBuilder
{
    public string Target => "dmarc-auth";
    public string RequiredPolicy => "DomainsEdit";
    public bool WritesToDomainZone => false;

    public async Task<DnsChangePlan> BuildAsync(DnsPushRequest request, CancellationToken cancellationToken)
    {
        // RFC 7489 §7.1: when the rua= mailbox's domain differs from the domain being monitored
        // (the normal MSP shape - a shared mailbox on the MSP's own domain, not each client's),
        // that mailbox's domain must publish this record proving it accepts reports for the
        // monitored domain. Unlike the "dmarc"/"tlsrpt" targets, this record's zone is the
        // MAILBOX's domain, not the monitored domain - ZoneName below reflects that, which is what
        // sends this push through whichever DNS provider hosts the deployment's own domain rather
        // than the client's.
        var mailbox = graphOptions.Value.MailboxAddress;
        var mailboxDomain = mailbox[(mailbox.IndexOf('@') + 1)..];
        var authorizationName = $"{request.Domain.Name}._report._dmarc.{mailboxDomain}";
        const string proposed = "v=DMARC1;";

        DnsProviderDetectionResult mailboxZoneDetection;
        try
        {
            mailboxZoneDetection = await dnsProviderDetector.DetectAsync(mailboxDomain, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return DnsChangePlan.Refuse("error");
        }

        var mailboxProviderKey = mailboxZoneDetection.Provider.ToProviderKey();
        if (!string.Equals(mailboxProviderKey, request.Provider, StringComparison.OrdinalIgnoreCase))
        {
            return DnsChangePlan.Refuse("zone-not-found");
        }

        var mailboxZone = mailboxZoneDetection.ZoneName;
        var existing = await dmarcAuthorizationTxtLookup.LookupAsync(authorizationName, cancellationToken).ConfigureAwait(false);
        if (existing.DelegatedToCname is not null)
        {
            return DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Replace, "TXT", authorizationName, proposed, existing.DelegatedToCname, mailboxZone, ExistingRecordType: "CNAME"));
        }

        if (existing.DirectValue is null)
        {
            return DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", authorizationName, proposed, null, mailboxZone));
        }

        // No structured tags to preserve here (unlike DMARC/TLSRPT's rua= merge) - an
        // authorization record's only job is to exist with v=DMARC1, so an unexpected
        // existing value is simply overwritten once the confirm dialog (shown for any
        // existing-differs-from-proposed case, per DnsRecordPushDecision.NeedsConfirmation)
        // has been accepted.
        return DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", authorizationName, proposed, existing.DirectValue, mailboxZone));
    }
}
