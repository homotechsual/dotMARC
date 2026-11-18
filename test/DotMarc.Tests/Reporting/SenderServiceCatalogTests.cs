using DotMarc.Dns;
using DotMarc.Reporting;
using Xunit;

namespace DotMarc.Tests.Reporting;

public sealed class SenderServiceCatalogTests
{
    private static SenderService Named(string name) => SenderServiceCatalog.All.Single(service => service.Name == name);

    [Fact]
    public void Names_AreUnique_AndEveryServiceHasAtLeastOneSignature()
    {
        Assert.Equal(SenderServiceCatalog.All.Count, SenderServiceCatalog.All.Select(service => service.Name).Distinct().Count());
        Assert.All(SenderServiceCatalog.All, service =>
            Assert.True(
                service.AuthDomainSuffixes.Count + service.DkimSelectorPatterns.Count + service.IpOwnerKeywords.Count > 0,
                $"{service.Name} has no signatures"));
    }

    [Theory]
    [InlineData("mcsv.net", true)]
    [InlineData("bounce.mcsv.net", true)]
    [InlineData("MCSV.NET", true)]
    [InlineData("notmcsv.net", false)]
    [InlineData("mcsv.net.evil.example", false)]
    public void MatchesAuthDomain_MatchesTheDomainItselfAndSubdomains_CaseInsensitively_ButNotLookalikes(string domain, bool expected) =>
        Assert.Equal(expected, SenderServiceCatalog.MatchesAuthDomain(Named("Mailchimp"), domain));

    [Theory]
    [InlineData("selector1", true)]
    [InlineData("Selector2", true)]
    [InlineData("selector10", false)]
    [InlineData("myselector1", false)]
    [InlineData(null, false)]
    public void MatchesSelector_IsAnchoredAndCaseInsensitive(string? selector, bool expected) =>
        Assert.Equal(expected, SenderServiceCatalog.MatchesSelector(Named("Microsoft 365"), selector));

    [Theory]
    [InlineData("Microsoft Corporation", true)]
    [InlineData("MICROSOFT-CORP-MSN-AS-BLOCK", true)]
    [InlineData("Google LLC", false)]
    [InlineData(null, false)]
    public void MatchesOwner_IsCaseInsensitiveContains(string? owner, bool expected) =>
        Assert.Equal(expected, SenderServiceCatalog.MatchesOwner(Named("Microsoft 365"), owner));

    [Theory]
    [InlineData("bounces.sendgrid.net", "SendGrid")]
    [InlineData("mail.mandrillapp.com", "Mandrill (Mailchimp Transactional)")]
    [InlineData("mail.example.com", null)]
    public void IdentifyByAuthDomains_ReturnsTheExistingNames(string domain, string? expected) =>
        Assert.Equal(expected, SenderServiceCatalog.IdentifyByAuthDomains([domain]));

    [Fact]
    public void EveryNameInSpfIncludeCatalog_IsInTheCatalogue()
    {
        var known = SenderServiceCatalog.All.SelectMany(service => service.DetectedNames.Append(service.Name)).ToHashSet();
        Assert.All(SpfIncludeCatalog.All, entry => Assert.Contains(entry.Name.Replace(" (EU)", ""), known));
    }

    [Fact]
    public void EveryIncludeInSpfIncludeCatalog_MapsToItsCatalogueService() =>
        Assert.All(SpfIncludeCatalog.All, entry =>
            Assert.Equal(entry.Name.Replace(" (EU)", ""), SenderServiceCatalog.ServiceForSpfInclude(entry.Include)));

    [Theory]
    [InlineData("spf.protection.outlook.com", "Microsoft 365")]
    [InlineData("_spf.google.com", "Google Workspace")]
    [InlineData("_netblocks.google.com", "Google Workspace")]
    [InlineData("SERVERS.MCSV.NET.", "Mailchimp")]
    [InlineData("sendgrid.net", "SendGrid")]
    [InlineData("mailgun.org", "Mailgun")]
    [InlineData("spf.mandrillapp.com", "Mandrill (Mailchimp Transactional)")]
    [InlineData("spf.mtasv.net", "Postmark")]
    [InlineData("sparkpostmail.com", "SparkPost")]
    [InlineData("hubspotemail.net", "HubSpot")]
    [InlineData("mail.zendesk.com", "Zendesk")]
    [InlineData("email.freshdesk.com", "Freshdesk")]
    [InlineData("amazonses.com", "Amazon SES")]
    [InlineData("_spf.salesforce.com", "Salesforce")]
    [InlineData("spf.constantcontact.com", "Constant Contact")]
    [InlineData("mail.unknown.example", null)]
    [InlineData("notgoogle.com", null)]
    [InlineData("_spf.google.com.evil.example", null)]
    public void ServiceForSpfInclude_MatchesTheHostOrItsSubdomains(string host, string? expected) =>
        Assert.Equal(expected, SenderServiceCatalog.ServiceForSpfInclude(host));

    [Theory]
    [InlineData("Microsoft 365")]
    [InlineData("Google Workspace")]
    [InlineData("Zoho")]
    [InlineData("Zoho Mail")]
    [InlineData("Fastmail")]
    [InlineData("ProtonMail")]
    [InlineData("Mailchimp")]
    [InlineData("SendGrid")]
    [InlineData("Amazon SES")]
    [InlineData("Salesforce")]
    public void EveryNameMailServiceDetectorUses_IsInTheCatalogue(string detectedName)
    {
        var known = SenderServiceCatalog.All.SelectMany(service => service.DetectedNames.Append(service.Name)).ToHashSet();
        Assert.Contains(detectedName, known);
    }
}
