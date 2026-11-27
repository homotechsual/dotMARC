using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>Checks whether a domain's DMARC records are correctly in place, querying Cloudflare's
/// DNS-over-HTTPS JSON API rather than whatever resolver the host happens to have configured - see
/// docs/superpowers/specs/2026-08-26-dmarc-dns-status-design.md for why. A waterfall, not two
/// independent lookups: each step only runs if the previous one passed, so a domain with no DMARC
/// record at all costs one query, not two.</summary>
public sealed class DmarcDnsChecker : IDmarcDnsChecker
{
    private readonly TxtRecordLookup _lookup;

    public DmarcDnsChecker(HttpClient http) => _lookup = new TxtRecordLookup(http);

    public async Task<DmarcCheckResult> CheckAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken, bool expectsReports = true)
    {
        var txtValues = await QueryAllTxtAsync($"_dmarc.{domainName}", cancellationToken).ConfigureAwait(false);
        var analysis = DmarcRecordAnalysis.Analyse(txtValues);
        if (analysis.Records.Count == 0)
        {
            return new DmarcCheckResult(DmarcCheckStatus.MissingOwnRecord, txtValues.Count > 0
                ? $"No DMARC record found at _dmarc.{domainName}"
                : $"No TXT record found at _dmarc.{domainName}");
        }

        // A policy is filled in for anything that looks like a DMARC record, a missing or invalid p reading as none.
        var policyLevel = analysis.Policy ?? DmarcPolicyLevel.None;
        var policy = new DmarcPolicyTags(policyLevel, analysis.EffectiveSubdomainPolicy ?? policyLevel, analysis.Percent ?? 100, analysis.NonExistentPolicy, analysis.Testing);

        // Breaking problems come first: receivers may ignore the record or read it as p=none, so the rua checks
        // below would be judging a record that does not work. Informational problems never change the status.
        var breaking = analysis.Problems.Where(problem => problem.Kind.IsBreaking()).ToList();
        if (breaking.Count > 0)
        {
            return new DmarcCheckResult(DmarcCheckStatus.Misconfigured,
                string.Join(" ", breaking.Select(problem => problem.Description)), policy, analysis);
        }

        if (!expectsReports)
            return new DmarcCheckResult(DmarcCheckStatus.Ok, "Valid DMARC record. Reporting to dotMARC is optional for a domain with null SPF.", policy, analysis);

        var ownRecord = analysis.Records[0];
        var ruaEntries = DmarcRua.Parse(ownRecord);

        // RFC 7489 section 6.2: rua= is a list of URIs, so every address needs its mailto: scheme. Receivers
        // skip an entry without one, so a bare address is a report destination that silently receives nothing.
        var withoutScheme = ruaEntries.Where(e => !e.HasMailtoScheme).Select(e => e.Address).ToList();
        if (withoutScheme.Count > 0)
        {
            return new DmarcCheckResult(DmarcCheckStatus.Misconfigured,
                $"_dmarc.{domainName}'s rua= has {string.Join(", ", withoutScheme)} without the mailto: prefix. "
                + "Every rua= address must start with mailto:, and receivers ignore any that does not.",
                policy, analysis);
        }

        var ruaAddresses = ruaEntries.Select(e => e.Address).ToList();
        if (!ruaAddresses.Any(a => string.Equals(a, mailboxAddress, StringComparison.OrdinalIgnoreCase)))
        {
            return new DmarcCheckResult(DmarcCheckStatus.Misconfigured,
                ruaAddresses.Count == 0
                    ? $"_dmarc.{domainName} has no rua= tag"
                    : $"_dmarc.{domainName}'s rua= points to {string.Join(", ", ruaAddresses)}, not {mailboxAddress}",
                policy, analysis);
        }

        // dotMARC's mailbox is in rua=. Any other addresses beside it are information while there are few, and a
        // fault once there are more than MaxReportAddresses: receivers are allowed to cap how many report
        // addresses they send to (RFC 7489 section 6.2), and some only honour the first couple.
        var distinctAddresses = ruaAddresses.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var otherAddresses = distinctAddresses
            .Where(a => !string.Equals(a, mailboxAddress, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (distinctAddresses.Count > MaxReportAddresses)
        {
            return new DmarcCheckResult(DmarcCheckStatus.TooManyReportAddresses,
                $"_dmarc.{domainName}'s rua= lists {distinctAddresses.Count} addresses ({string.Join(", ", distinctAddresses)}). "
                + $"Some receivers only send reports to the first {MaxReportAddresses}, so reports may not reach {mailboxAddress}. "
                + $"Keep {mailboxAddress} and at most {MaxReportAddresses - 1} other address.",
                policy, analysis);
        }

        if (otherAddresses.Count > 0)
        {
            return new DmarcCheckResult(DmarcCheckStatus.OtherReportAddresses,
                $"_dmarc.{domainName}'s rua= also sends reports to {string.Join(", ", otherAddresses)}. "
                + "That is fine while you compare tools, and a DNS push keeps it by default. "
                + "To drop it, turn off \"Keep other rua addresses\" in the DNS push settings and push the record.",
                policy, analysis);
        }

        return new DmarcCheckResult(DmarcCheckStatus.Ok, null, policy, analysis);
    }

    /// <summary>The most rua= addresses a record can list before reports may stop reaching all of them. Receivers
    /// may cap the count (RFC 7489 section 6.2) and some only send to the first two. dotMARC's own address counts.</summary>
    public const int MaxReportAddresses = 2;

    /// <summary>RFC 9990 §4: when the rua= mailbox's domain differs from the domain being
    /// monitored (the normal MSP shape), that mailbox's domain must publish this record proving it
    /// accepts reports for the monitored domain. Independent of CheckAsync above - this always
    /// runs and always returns its own result, regardless of whether CheckAsync's own-record check
    /// passed or failed, so both can be shown (and separately corrected) at once.</summary>
    public async Task<DmarcAuthorizationCheckResult> CheckAuthorizationAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken)
    {
        var mailboxDomain = mailboxAddress[(mailboxAddress.IndexOf('@') + 1)..];
        if (string.Equals(mailboxDomain, domainName, StringComparison.OrdinalIgnoreCase))
        {
            return new DmarcAuthorizationCheckResult(DmarcAuthorizationCheckStatus.NotApplicable, null);
        }

        var authorizationName = $"{domainName}._report._dmarc.{mailboxDomain}";
        var authorizationRecord = await QueryTxtAsync(authorizationName, cancellationToken).ConfigureAwait(false);
        return authorizationRecord is null
            ? new DmarcAuthorizationCheckResult(DmarcAuthorizationCheckStatus.Missing, $"No TXT record found at {authorizationName}")
            : new DmarcAuthorizationCheckResult(DmarcAuthorizationCheckStatus.Ok, null);
    }

    /// <summary>Returns the first TXT record's value (quotes stripped, multi-segment values
    /// joined), or null for a successful NODATA or NXDOMAIN lookup. DNS errors throw.</summary>
    private async Task<string?> QueryTxtAsync(string name, CancellationToken cancellationToken) =>
        (await QueryAllTxtAsync(name, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    /// <summary>Returns every TXT record's value at the name (same format as <see cref="QueryTxtAsync"/>), in the
    /// order DNS gave them; empty when there are none.</summary>
    private async Task<List<string>> QueryAllTxtAsync(string name, CancellationToken cancellationToken) =>
        (await _lookup.GetTxtValuesAsync(name, cancellationToken).ConfigureAwait(false)).ToList();
}
