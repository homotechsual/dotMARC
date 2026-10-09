using DotMarc.Data;
using DotMarc.Psa;
using Xunit;

namespace DotMarc.Tests.Psa;

public sealed class PsaCompanyResolverTests
{
    private static PsaCompanyLink Link(PsaKind psa, string companyId) => new() { Psa = psa, CompanyId = companyId, CompanyName = $"Company {companyId}" };

    private static Group GroupWith(int id, params PsaCompanyLink[] links) => new() { Id = id, Name = $"Group {id}", PsaCompanyLinks = [.. links] };

    private static Domain DomainWith(IEnumerable<Group> groups, params PsaCompanyLink[] links) =>
        new() { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow, Groups = [.. groups], PsaCompanyLinks = [.. links] };

    [Fact]
    public void TheDomainsOwnLink_WinsOverItsGroups_AndNoGroupDecides()
    {
        var domain = DomainWith([GroupWith(1, Link(PsaKind.HaloPsa, "7"))], Link(PsaKind.HaloPsa, "9"));

        Assert.Equal("9", PsaCompanyResolver.Resolve(domain, PsaKind.HaloPsa)?.CompanyId);
        Assert.Null(PsaCompanyResolver.ResolveGroup(domain, PsaKind.HaloPsa));
    }

    [Fact]
    public void WithoutItsOwnLink_TheLowestIdGroupLinkedInThatPsaDecides()
    {
        var domain = DomainWith([GroupWith(5, Link(PsaKind.HaloPsa, "50")), GroupWith(2, Link(PsaKind.HaloPsa, "20")), GroupWith(1)]);

        Assert.Equal("20", PsaCompanyResolver.Resolve(domain, PsaKind.HaloPsa)?.CompanyId);
        Assert.Equal(2, PsaCompanyResolver.ResolveGroup(domain, PsaKind.HaloPsa)?.Id);
    }

    [Fact]
    public void EachPsa_IsResolvedOnItsOwn_SoDifferentGroupsCanDecide()
    {
        var haloGroup = GroupWith(1, Link(PsaKind.HaloPsa, "7"));
        var connectWiseGroup = GroupWith(2, Link(PsaKind.ConnectWise, "250"));
        var domain = DomainWith([haloGroup, connectWiseGroup], Link(PsaKind.Autotask, "3001"));

        Assert.Equal(1, PsaCompanyResolver.ResolveGroup(domain, PsaKind.HaloPsa)?.Id);
        Assert.Equal(2, PsaCompanyResolver.ResolveGroup(domain, PsaKind.ConnectWise)?.Id);
        Assert.Null(PsaCompanyResolver.ResolveGroup(domain, PsaKind.Autotask));
        Assert.Equal("3001", PsaCompanyResolver.Resolve(domain, PsaKind.Autotask)?.CompanyId);
    }

    [Fact]
    public void NoLinkAnywhere_ResolvesToNothing()
    {
        var domain = DomainWith([GroupWith(1, Link(PsaKind.HaloPsa, "7"))]);

        Assert.Null(PsaCompanyResolver.Resolve(domain, PsaKind.ConnectWise));
        Assert.Null(PsaCompanyResolver.ResolveGroup(domain, PsaKind.ConnectWise));
    }
}
