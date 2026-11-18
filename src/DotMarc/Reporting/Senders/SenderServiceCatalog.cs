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
    IReadOnlyList<string> DetectedNames);

public static class SenderServiceCatalog
{
    private static SenderService Service(string name, string[]? authDomains = null, string[]? selectors = null, string[]? owners = null, string[]? detectedNames = null) =>
        new(name, authDomains ?? [], selectors ?? [], owners ?? [], detectedNames ?? []);

    /// <summary>In priority order: when two services score the same for a source, the earlier one wins. Aligned senders sign
    /// as the customer's own domain, so their auth domains match nothing here and the selector and IP owner carry them.</summary>
    public static IReadOnlyList<SenderService> All { get; } =
    [
        Service("Microsoft 365", ["onmicrosoft.com", "outlook.com"], ["^selector[12]$"], ["Microsoft"]),
        Service("Google Workspace", ["google.com", "gmail.com", "googlemail.com"], ["^google$"], ["Google"]),
        Service("Zoho", ["zoho.com", "zoho.eu"], [], ["Zoho"], ["Zoho Mail"]),
        Service("Fastmail", ["messagingengine.com", "fastmail.com"], [], ["Fastmail"]),
        Service("Proton Mail", ["protonmail.ch", "proton.ch"], [], ["Proton"], ["ProtonMail"]),
        Service("SendGrid", ["sendgrid.net"], ["^s[12]$"], ["SendGrid"]),
        Service("Mailgun", ["mailgun.org"], [], ["Mailgun"]),
        Service("Mailchimp", ["mcsv.net"], ["^k[1-3]$"], ["Rocket Science"]),
        Service("Mandrill (Mailchimp Transactional)", ["mandrillapp.com"], ["^mte[12]$"], []),
        Service("Salesforce", ["salesforce.com"], [], ["Salesforce"]),
        Service("Salesforce Marketing Cloud", ["exacttarget.com"], [], []),
        Service("Zendesk", ["zendesk.com"], ["^zendesk[12]$"], ["Zendesk"]),
        Service("HubSpot", ["hubspotemail.net"], ["^hs[12]$"], ["HubSpot"]),
        Service("Freshdesk", ["freshdesk.com"], [], []),
        Service("Constant Contact", ["constantcontact.com"], [], []),
        Service("SparkPost", ["sparkpostmail.com"], [], []),
        Service("Postmark", ["postmarkapp.com"], [], []),
        Service("Amazon SES", ["amazonses.com"], [], ["Amazon"]),
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

    /// <summary>The name of the first service whose auth domains match any of <paramref name="domains"/>, or null.</summary>
    public static string? IdentifyByAuthDomains(IEnumerable<string> domains)
    {
        var hosts = domains.ToList();
        return All.FirstOrDefault(service => hosts.Any(host => MatchesAuthDomain(service, host)))?.Name;
    }
}
