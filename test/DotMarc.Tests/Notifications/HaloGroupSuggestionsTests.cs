using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class HaloGroupSuggestionsTests
{
    private static readonly HaloClient Compute = new(37, "Compute (Bridgend) Limited");
    private static readonly HaloClient Mcgarvey = new(14, "McGarvey Immigration & Asylum Practitioners");
    private static readonly HaloClient CherylHarvey = new(144, "Cheryl Harvey");
    private static readonly HaloClient LindaHarvey = new(145, "Linda Harvey");
    private static readonly HaloClient TechPulse = new(99, "TechPulse Consulting LLC");
    private static readonly HaloClient Unknown = new(1, "Unknown");

    private static readonly HaloClient[] AllClients = [Compute, Mcgarvey, CherylHarvey, LindaHarvey, TechPulse, Unknown];

    [Theory]
    [InlineData("Compute Bridgend", "Compute (Bridgend) Limited")]
    [InlineData("compute (bridgend) ltd", "Compute (Bridgend) Limited")]
    [InlineData("TechPulse Consulting", "TechPulse Consulting LLC")]
    [InlineData("McGarvey", "McGarvey Immigration & Asylum Practitioners")]
    [InlineData("McGarvey Immigration and Asylum Practitioners", "McGarvey Immigration & Asylum Practitioners")]
    public void NamesMatch_IgnoresCasePunctuationAndCompanySuffixes(string groupName, string clientName)
    {
        Assert.True(HaloGroupSuggestions.NamesMatch(groupName, clientName));
    }

    [Theory]
    [InlineData("Harvey", "Cheryl Harvey")]
    [InlineData("Compute", "TechPulse Consulting LLC")]
    [InlineData("", "Compute (Bridgend) Limited")]
    [InlineData("Limited", "Compute (Bridgend) Limited")]
    public void NamesMatch_DoesNotTreatASharedWordInTheMiddleOrEndAsAMatch(string groupName, string clientName)
    {
        Assert.False(HaloGroupSuggestions.NamesMatch(groupName, clientName));
    }

    [Fact]
    public void ClientsWithoutGroup_OffersEveryClientWhenThereAreNoGroups()
    {
        var suggestions = HaloGroupSuggestions.ClientsWithoutGroup(AllClients, []);

        Assert.Equal(["Cheryl Harvey", "Compute (Bridgend) Limited", "Linda Harvey", "McGarvey Immigration & Asylum Practitioners", "TechPulse Consulting LLC"],
            suggestions.Select(client => client.Name));
    }

    [Fact]
    public void ClientsWithoutGroup_LeavesOutHalosBuiltInUnknownClient()
    {
        var suggestions = HaloGroupSuggestions.ClientsWithoutGroup(AllClients, []);

        Assert.DoesNotContain(suggestions, client => client.Id == HaloGroupSuggestions.UnknownClientId);
    }

    [Fact]
    public void ClientsWithoutGroup_LeavesOutClientsAlreadyLinkedToAGroupWhateverTheGroupIsCalled()
    {
        var groups = new[] { new GroupSummary("Bridgend office", Compute.Id) };

        var suggestions = HaloGroupSuggestions.ClientsWithoutGroup(AllClients, groups);

        Assert.DoesNotContain(suggestions, client => client.Id == Compute.Id);
    }

    [Fact]
    public void ClientsWithoutGroup_LeavesOutClientsWhoseNameMatchesAnUnlinkedGroup()
    {
        var groups = new[] { new GroupSummary("Compute Bridgend", null), new GroupSummary("McGarvey", null) };

        var suggestions = HaloGroupSuggestions.ClientsWithoutGroup(AllClients, groups);

        Assert.DoesNotContain(suggestions, client => client.Id == Compute.Id || client.Id == Mcgarvey.Id);
        Assert.Contains(suggestions, client => client.Id == TechPulse.Id);
    }

    [Fact]
    public void ClientsWithoutGroup_KeepsBothHarveysWhenTheGroupIsJustHarvey()
    {
        var groups = new[] { new GroupSummary("Harvey", null) };

        var suggestions = HaloGroupSuggestions.ClientsWithoutGroup(AllClients, groups);

        Assert.Contains(suggestions, client => client.Id == CherylHarvey.Id);
        Assert.Contains(suggestions, client => client.Id == LindaHarvey.Id);
    }

    [Fact]
    public void ClientsWithoutGroup_IsEmptyWhenEveryClientHasAGroup()
    {
        var groups = AllClients.Select(client => new GroupSummary(client.Name, client.Id));

        Assert.Empty(HaloGroupSuggestions.ClientsWithoutGroup(AllClients, groups));
    }

    [Fact]
    public void SuggestClientsForGroup_PutsTheExactMatchFirstThenClientsThatStartWithTheName()
    {
        var clients = new[] { new HaloClient(50, "Acme Holdings"), new HaloClient(51, "Acme") };

        var suggestions = HaloGroupSuggestions.SuggestClientsForGroup("Acme", clients);

        Assert.Equal([51, 50], suggestions.Select(client => client.Id));
    }

    [Fact]
    public void SuggestClientsForGroup_FindsAClientDespiteLimitedAndBrackets()
    {
        var suggestions = HaloGroupSuggestions.SuggestClientsForGroup("Compute Bridgend", AllClients);

        Assert.Equal([Compute.Id], suggestions.Select(client => client.Id));
    }

    [Fact]
    public void SuggestClientsForGroup_NeverSuggestsTheUnknownClient()
    {
        Assert.Empty(HaloGroupSuggestions.SuggestClientsForGroup("Unknown", AllClients));
    }
}
