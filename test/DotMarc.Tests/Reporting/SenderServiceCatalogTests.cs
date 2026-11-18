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
