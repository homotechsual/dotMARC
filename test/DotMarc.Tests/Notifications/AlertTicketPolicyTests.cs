// test/DotMarc.Tests/Notifications/AlertTicketPolicyTests.cs
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Psa;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class AlertTicketPolicyTests
{
    private static List<PsaCompanyLink> HaloLink(int? haloClientId) =>
        haloClientId is { } id ? [new PsaCompanyLink { Psa = PsaKind.HaloPsa, CompanyId = id.ToString(), CompanyName = $"Client {id}" }] : [];

    private static Group MakeGroup(int id, int? haloClientId) => new() { Id = id, Name = $"Group {id}", PsaCompanyLinks = HaloLink(haloClientId) };

    private static Domain DomainIn(int? clientOverride = null, params Group[] groups) =>
        new() { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow, PsaCompanyLinks = HaloLink(clientOverride), Groups = [.. groups] };

    private static AlertTicketRule GlobalRule(string alertType, bool createTicket) =>
        new() { AlertType = alertType, GroupId = null, CreateTicket = createTicket };

    private static AlertTicketRule GroupRule(int groupId, string alertType, bool createTicket) =>
        new() { AlertType = alertType, GroupId = groupId, CreateTicket = createTicket };

    [Fact]
    public void WithNoRules_EachAlertTypeFollowsItsTicketDefault()
    {
        var domain = DomainIn(null, MakeGroup(1, 7));

        foreach (var alertType in AlertTypes.All)
        {
            Assert.Equal(alertType.CreatesTicketByDefault, AlertTicketPolicy.ShouldCreateTicket(alertType.Key, domain, PsaKind.HaloPsa, []));
        }

        // Only nameserver changes, a heads-up rather than a fault, default to no ticket.
        Assert.False(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.NameserversChanged, domain, PsaKind.HaloPsa, []));
        Assert.True(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.SpfRecordBroken, domain, PsaKind.HaloPsa, []));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AGlobalRule_DecidesWhenTheGroupHasNoOverride(bool globalCreates)
    {
        var domain = DomainIn(null, MakeGroup(1, 7));

        var result = AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, PsaKind.HaloPsa, [GlobalRule(AlertTypes.MissedReport, globalCreates)]);

        Assert.Equal(globalCreates, result);
    }

    [Fact]
    public void AGroupOverrideOfNever_BeatsAGlobalYes()
    {
        var domain = DomainIn(null, MakeGroup(1, 7));
        AlertTicketRule[] rules = [GlobalRule(AlertTypes.MissedReport, true), GroupRule(1, AlertTypes.MissedReport, false)];

        Assert.False(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, PsaKind.HaloPsa, rules));
    }

    [Fact]
    public void AGroupOverrideOfAlways_BeatsAGlobalNo()
    {
        var domain = DomainIn(null, MakeGroup(1, 7));
        AlertTicketRule[] rules = [GlobalRule(AlertTypes.MissedReport, false), GroupRule(1, AlertTypes.MissedReport, true)];

        Assert.True(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, PsaKind.HaloPsa, rules));
    }

    [Fact]
    public void AGroupOverride_OnlyAffectsItsOwnAlertType()
    {
        var domain = DomainIn(null, MakeGroup(1, 7));
        AlertTicketRule[] rules = [GroupRule(1, AlertTypes.MissedReport, false)];

        Assert.False(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, PsaKind.HaloPsa, rules));
        Assert.True(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.TlsrptFailure, domain, PsaKind.HaloPsa, rules));
    }

    [Fact]
    public void ADomainWithItsOwnHaloClient_IgnoresEveryGroupOverride_AndUsesTheGlobalRule()
    {
        var domain = DomainIn(clientOverride: 99, MakeGroup(1, 7));
        AlertTicketRule[] rules = [GlobalRule(AlertTypes.MissedReport, true), GroupRule(1, AlertTypes.MissedReport, false)];

        Assert.True(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, PsaKind.HaloPsa, rules));
    }

    [Fact]
    public void AGroupWithNoHaloClient_IsNotTheDecidingGroup()
    {
        // Group 1 has no client, so group 2 decides where the ticket goes, and group 2's rules apply.
        var domain = DomainIn(null, MakeGroup(1, null), MakeGroup(2, 8));
        AlertTicketRule[] rules = [GroupRule(1, AlertTypes.MissedReport, false), GroupRule(2, AlertTypes.MissedReport, true), GlobalRule(AlertTypes.MissedReport, false)];

        Assert.True(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, PsaKind.HaloPsa, rules));
    }

    [Fact]
    public void WithSeveralGroups_TheDecidingGroupsRuleWins_NotAnotherGroupsRule()
    {
        // Group 3 (the lowest id with a client is group 2) says Yes; group 2 says No. Group 2 decides.
        var domain = DomainIn(null, MakeGroup(3, 9), MakeGroup(2, 8));
        AlertTicketRule[] rules = [GroupRule(2, AlertTypes.MissedReport, false), GroupRule(3, AlertTypes.MissedReport, true)];

        Assert.False(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, PsaKind.HaloPsa, rules));
    }

    [Fact]
    public void ADomainWithNoGroups_UsesTheGlobalRule()
    {
        var domain = DomainIn();

        Assert.False(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, PsaKind.HaloPsa, [GlobalRule(AlertTypes.MissedReport, false)]));
    }

    [Fact]
    public void ARuleForAnUnknownAlertType_IsIgnored()
    {
        var domain = DomainIn(null, MakeGroup(1, 7));
        AlertTicketRule[] rules = [GlobalRule("RetiredAlertType", false), GroupRule(1, "RetiredAlertType", false)];

        Assert.True(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, PsaKind.HaloPsa, rules));
    }

    [Fact]
    public void AnAlertTypeMissingFromTheRegistry_CreatesATicketUnlessARuleSaysOtherwise()
    {
        var domain = DomainIn(null, MakeGroup(1, 7));

        Assert.True(AlertTicketPolicy.ShouldCreateTicket("BrandNewType", domain, PsaKind.HaloPsa, []));
        Assert.False(AlertTicketPolicy.ShouldCreateTicket("BrandNewType", domain, PsaKind.HaloPsa, [GlobalRule("BrandNewType", false)]));
    }

    [Fact]
    public void ResolveGroup_PicksTheLowestIdGroupWithAClient_AndNullWhenTheDomainHasItsOwnClient()
    {
        var withGroups = DomainIn(null, MakeGroup(5, 9), MakeGroup(2, 8), MakeGroup(1, null));
        var withOverride = DomainIn(clientOverride: 99, MakeGroup(2, 8));

        Assert.Equal(2, PsaCompanyResolver.ResolveGroup(withGroups, PsaKind.HaloPsa)!.Id);
        Assert.Null(PsaCompanyResolver.ResolveGroup(withOverride, PsaKind.HaloPsa));
        Assert.Null(PsaCompanyResolver.ResolveGroup(DomainIn(null, MakeGroup(1, null)), PsaKind.HaloPsa));
    }

    [Fact]
    public void AGroupRule_OnlyAppliesToThePsaThatGroupDecides()
    {
        var haloGroup = new Group { Id = 1, Name = "Halo group", PsaCompanyLinks = [new PsaCompanyLink { Psa = PsaKind.HaloPsa, CompanyId = "7", CompanyName = "A" }] };
        var connectWiseGroup = new Group { Id = 2, Name = "CW group", PsaCompanyLinks = [new PsaCompanyLink { Psa = PsaKind.ConnectWise, CompanyId = "250", CompanyName = "B" }] };
        var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow, Groups = [haloGroup, connectWiseGroup] };
        AlertTicketRule[] rules = [GroupRule(1, AlertTypes.MissedReport, false)];

        Assert.False(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, PsaKind.HaloPsa, rules));
        Assert.True(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, PsaKind.ConnectWise, rules));
    }
}
