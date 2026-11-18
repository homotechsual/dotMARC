using System.Text.RegularExpressions;

namespace DotMarc.Reporting;

/// <summary>One sending service and the signatures that identify it in a DMARC report. The names match the ones
/// <c>MailServiceDetector</c>, <c>SpfIncludeCatalog</c> and the old recommendation list used, so the Dashboard, the DNS
/// detection and the sender table all agree. <see cref="DetectedNames"/> are the other names the DNS detection gives it.</summary>
public sealed record SenderService(
    string Name,
    IReadOnlyList<string> AuthDomainSuffixes,
    IReadOnlyList<string> DkimSelectorPatterns,
    IReadOnlyList<string> IpOwnerKeywords,
    IReadOnlyList<string> DetectedNames,
    IReadOnlyList<string> SpfIncludeHosts);

public static class SenderServiceCatalog
{
    private static SenderService Service(string name, string[]? authDomains = null, string[]? selectors = null, string[]? owners = null, string[]? detectedNames = null, string[]? spfIncludes = null) =>
        new(name, authDomains ?? [], selectors ?? [], owners ?? [], detectedNames ?? [], spfIncludes ?? []);

    /// <summary>In priority order: when two services score the same for a source, the earlier one wins. Aligned senders sign
    /// as the customer's own domain, so their auth domains match nothing here and the selector and IP owner carry them.</summary>
    public static IReadOnlyList<SenderService> All { get; } =
    [
        Service("Microsoft 365", ["onmicrosoft.com", "outlook.com"], ["^selector[12]$"], ["Microsoft"], null, spfIncludes: ["spf.protection.outlook.com"]),
        Service("Google Workspace", ["google.com", "gmail.com", "googlemail.com"], ["^google$"], ["Google"], null, spfIncludes: ["_spf.google.com", "_netblocks.google.com"]),
        Service("Zoho", ["zoho.com", "zoho.eu"], [], ["Zoho"], ["Zoho Mail"], spfIncludes: ["zoho.com", "zoho.eu"]),
        Service("Fastmail", ["messagingengine.com", "fastmail.com"], [], ["Fastmail"], null, spfIncludes: ["spf.messagingengine.com"]),
        Service("Proton Mail", ["protonmail.ch", "proton.ch"], [], ["Proton"], ["ProtonMail"], spfIncludes: ["_spf.protonmail.ch"]),
        Service("SendGrid", ["sendgrid.net"], ["^s[12]$"], ["SendGrid"], null, spfIncludes: ["sendgrid.net"]),
        Service("Mailgun", ["mailgun.org"], [], ["Mailgun"], null, spfIncludes: ["mailgun.org"]),
        Service("Mailchimp", ["mcsv.net"], ["^k[1-3]$"], ["Rocket Science"], null, spfIncludes: ["servers.mcsv.net"]),
        Service("Mandrill (Mailchimp Transactional)", ["mandrillapp.com"], ["^mte[12]$"], [], null, spfIncludes: ["spf.mandrillapp.com"]),
        Service("Salesforce", ["salesforce.com"], [], ["Salesforce"], null, spfIncludes: ["_spf.salesforce.com"]),
        Service("Salesforce Marketing Cloud", ["exacttarget.com"], [], []),
        Service("Zendesk", ["zendesk.com"], ["^zendesk[12]$"], ["Zendesk"], null, spfIncludes: ["mail.zendesk.com"]),
        Service("HubSpot", ["hubspotemail.net"], ["^hs[12]$"], ["HubSpot"], null, spfIncludes: ["hubspotemail.net"]),
        Service("Freshdesk", ["freshdesk.com"], [], [], null, spfIncludes: ["email.freshdesk.com"]),
        Service("Constant Contact", ["constantcontact.com"], [], [], null, spfIncludes: ["spf.constantcontact.com"]),
        Service("SparkPost", ["sparkpostmail.com"], [], [], null, spfIncludes: ["sparkpostmail.com"]),
        Service("Postmark", ["postmarkapp.com"], [], [], null, spfIncludes: ["spf.mtasv.net"]),
        Service("Amazon SES", ["amazonses.com"], [], ["Amazon"], null, spfIncludes: ["amazonses.com"]),
    ];

    public static bool MatchesAuthDomain(SenderService service, string domain)
    {
        var host = domain.Trim().TrimEnd('.');
        return service.AuthDomainSuffixes.Any(suffix =>
            host.Equals(suffix, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
    }

    public static bool MatchesSelector(SenderService service, string? selector) =>
        !string.IsNullOrWhiteSpace(selector)
        && service.DkimSelectorPatterns.Any(pattern => Regex.IsMatch(selector.Trim(), pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    public static bool MatchesOwner(SenderService service, string? owner) =>
        !string.IsNullOrWhiteSpace(owner)
        && service.IpOwnerKeywords.Any(keyword => owner.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    /// <summary>The service a host in an SPF record belongs to (the host itself or any subdomain of one of its include hosts), or null.</summary>
    public static string? ServiceForSpfInclude(string host)
    {
        var name = host.Trim().TrimEnd('.');
        return All.FirstOrDefault(service => service.SpfIncludeHosts.Any(entry =>
            name.Equals(entry, StringComparison.OrdinalIgnoreCase) || name.EndsWith("." + entry, StringComparison.OrdinalIgnoreCase)))?.Name;
    }

    /// <summary>The name of the first service whose auth domains match any of <paramref name="domains"/>, or null.</summary>
    public static string? IdentifyByAuthDomains(IEnumerable<string> domains)
    {
        var hosts = domains.ToList();
        return All.FirstOrDefault(service => hosts.Any(host => MatchesAuthDomain(service, host)))?.Name;
    }
}
