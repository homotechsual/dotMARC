// test/DotMarc.Tests/Notifications/AlertTicketPolicyTests.cs
using DotMarc.Data;
using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class AlertTicketPolicyTests
{
    private static Group MakeGroup(int id, int? haloClientId) => new() { Id = id, Name = $"Group {id}", HaloClientId = haloClientId };

    private static Domain DomainIn(int? clientOverride = null, params Group[] groups) =>
        new() { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow, HaloClientId = clientOverride, Groups = [.. groups] };

    private static AlertTicketRule GlobalRule(string alertType, bool createTicket) =>
        new() { AlertType = alertType, GroupId = null, CreateTicket = createTicket };

    private static AlertTicketRule GroupRule(int groupId, string alertType, bool createTicket) =>
        new() { AlertType = alertType, GroupId = groupId, CreateTicket = createTicket };

    [Fact]
    public void WithNoRules_EveryAlertTypeCreatesATicket()
    {
        var domain = DomainIn(null, MakeGroup(1, 7));

        foreach (var alertType in AlertTypes.All)
        {
            Assert.True(AlertTicketPolicy.ShouldCreateTicket(alertType.Key, domain, []));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AGlobalRule_DecidesWhenTheGroupHasNoOverride(bool globalCreates)
    {
        var domain = DomainIn(null, MakeGroup(1, 7));

        var result = AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, [GlobalRule(AlertTypes.MissedReport, globalCreates)]);

        Assert.Equal(globalCreates, result);
    }

    [Fact]
    public void AGroupOverrideOfNever_BeatsAGlobalYes()
    {
        var domain = DomainIn(null, MakeGroup(1, 7));
        AlertTicketRule[] rules = [GlobalRule(AlertTypes.MissedReport, true), GroupRule(1, AlertTypes.MissedReport, false)];

        Assert.False(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, rules));
    }

    [Fact]
    public void AGroupOverrideOfAlways_BeatsAGlobalNo()
    {
        var domain = DomainIn(null, MakeGroup(1, 7));
        AlertTicketRule[] rules = [GlobalRule(AlertTypes.MissedReport, false), GroupRule(1, AlertTypes.MissedReport, true)];

        Assert.True(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, rules));
    }

    [Fact]
    public void AGroupOverride_OnlyAffectsItsOwnAlertType()
    {
        var domain = DomainIn(null, MakeGroup(1, 7));
        AlertTicketRule[] rules = [GroupRule(1, AlertTypes.MissedReport, false)];

        Assert.False(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, rules));
        Assert.True(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.TlsrptFailure, domain, rules));
    }

    [Fact]
    public void ADomainWithItsOwnHaloClient_IgnoresEveryGroupOverride_AndUsesTheGlobalRule()
    {
        var domain = DomainIn(clientOverride: 99, MakeGroup(1, 7));
        AlertTicketRule[] rules = [GlobalRule(AlertTypes.MissedReport, true), GroupRule(1, AlertTypes.MissedReport, false)];

        Assert.True(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, rules));
    }

    [Fact]
    public void AGroupWithNoHaloClient_IsNotTheDecidingGroup()
    {
        // Group 1 has no client, so group 2 decides where the ticket goes, and group 2's rules apply.
        var domain = DomainIn(null, MakeGroup(1, null), MakeGroup(2, 8));
        AlertTicketRule[] rules = [GroupRule(1, AlertTypes.MissedReport, false), GroupRule(2, AlertTypes.MissedReport, true), GlobalRule(AlertTypes.MissedReport, false)];

        Assert.True(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, rules));
    }

    [Fact]
    public void WithSeveralGroups_TheDecidingGroupsRuleWins_NotAnotherGroupsRule()
    {
        // Group 3 (the lowest id with a client is group 2) says Yes; group 2 says No. Group 2 decides.
        var domain = DomainIn(null, MakeGroup(3, 9), MakeGroup(2, 8));
        AlertTicketRule[] rules = [GroupRule(2, AlertTypes.MissedReport, false), GroupRule(3, AlertTypes.MissedReport, true)];

        Assert.False(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, rules));
    }

    [Fact]
    public void ADomainWithNoGroups_UsesTheGlobalRule()
    {
        var domain = DomainIn();

        Assert.False(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, [GlobalRule(AlertTypes.MissedReport, false)]));
    }

    [Fact]
    public void ARuleForAnUnknownAlertType_IsIgnored()
    {
        var domain = DomainIn(null, MakeGroup(1, 7));
        AlertTicketRule[] rules = [GlobalRule("RetiredAlertType", false), GroupRule(1, "RetiredAlertType", false)];

        Assert.True(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, rules));
    }

    [Fact]
    public void AnAlertTypeMissingFromTheRegistry_CreatesATicketUnlessARuleSaysOtherwise()
    {
        var domain = DomainIn(null, MakeGroup(1, 7));

        Assert.True(AlertTicketPolicy.ShouldCreateTicket("BrandNewType", domain, []));
        Assert.False(AlertTicketPolicy.ShouldCreateTicket("BrandNewType", domain, [GlobalRule("BrandNewType", false)]));
    }

    [Fact]
    public void ResolveGroup_PicksTheLowestIdGroupWithAClient_AndNullWhenTheDomainHasItsOwnClient()
    {
        var withGroups = DomainIn(null, MakeGroup(5, 9), MakeGroup(2, 8), MakeGroup(1, null));
        var withOverride = DomainIn(clientOverride: 99, MakeGroup(2, 8));

        Assert.Equal(2, HaloClientResolver.ResolveGroup(withGroups)!.Id);
        Assert.Null(HaloClientResolver.ResolveGroup(withOverride));
        Assert.Null(HaloClientResolver.ResolveGroup(DomainIn(null, MakeGroup(1, null))));
    }
}
