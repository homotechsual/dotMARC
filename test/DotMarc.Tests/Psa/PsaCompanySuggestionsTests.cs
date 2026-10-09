using DotMarc.Psa;
using Xunit;

namespace DotMarc.Tests.Psa;

public sealed class PsaCompanySuggestionsTests
{
    private static readonly PsaCompany Compute = new("37", "Compute (Bridgend) Limited");
    private static readonly PsaCompany Mcgarvey = new("14", "McGarvey Immigration & Asylum Practitioners");
    private static readonly PsaCompany CherylHarvey = new("144", "Cheryl Harvey");
    private static readonly PsaCompany LindaHarvey = new("145", "Linda Harvey");
    private static readonly PsaCompany TechPulse = new("99", "TechPulse Consulting LLC");
    private static readonly PsaCompany Unknown = new("1", "Unknown");

    private static readonly PsaCompany[] AllClients = [Compute, Mcgarvey, CherylHarvey, LindaHarvey, TechPulse, Unknown];

    [Theory]
    [InlineData("Compute Bridgend", "Compute (Bridgend) Limited")]
    [InlineData("compute (bridgend) ltd", "Compute (Bridgend) Limited")]
    [InlineData("TechPulse Consulting", "TechPulse Consulting LLC")]
    [InlineData("McGarvey", "McGarvey Immigration & Asylum Practitioners")]
    [InlineData("McGarvey Immigration and Asylum Practitioners", "McGarvey Immigration & Asylum Practitioners")]
    public void NamesMatch_IgnoresCasePunctuationAndCompanySuffixes(string groupName, string clientName)
    {
        Assert.True(PsaCompanySuggestions.NamesMatch(groupName, clientName));
    }

    [Theory]
    [InlineData("Harvey", "Cheryl Harvey")]
    [InlineData("Compute", "TechPulse Consulting LLC")]
    [InlineData("", "Compute (Bridgend) Limited")]
    [InlineData("Limited", "Compute (Bridgend) Limited")]
    public void NamesMatch_DoesNotTreatASharedWordInTheMiddleOrEndAsAMatch(string groupName, string clientName)
    {
        Assert.False(PsaCompanySuggestions.NamesMatch(groupName, clientName));
    }

    [Fact]
    public void ClientsWithoutGroup_OffersEveryClientWhenThereAreNoGroups()
    {
        var suggestions = PsaCompanySuggestions.CompaniesWithoutGroup(PsaKind.HaloPsa, AllClients, []);

        Assert.Equal(["Cheryl Harvey", "Compute (Bridgend) Limited", "Linda Harvey", "McGarvey Immigration & Asylum Practitioners", "TechPulse Consulting LLC"],
            suggestions.Select(client => client.Name));
    }

    [Fact]
    public void ClientsWithoutGroup_LeavesOutHalosBuiltInUnknownClient()
    {
        var suggestions = PsaCompanySuggestions.CompaniesWithoutGroup(PsaKind.HaloPsa, AllClients, []);

        Assert.DoesNotContain(suggestions, client => client.Id == PsaCompanySuggestions.HaloUnknownClientId);
    }

    [Fact]
    public void ClientsWithoutGroup_LeavesOutClientsAlreadyLinkedToAGroupWhateverTheGroupIsCalled()
    {
        var groups = new[] { new GroupSummary("Bridgend office", Compute.Id) };

        var suggestions = PsaCompanySuggestions.CompaniesWithoutGroup(PsaKind.HaloPsa, AllClients, groups);

        Assert.DoesNotContain(suggestions, client => client.Id == Compute.Id);
    }

    [Fact]
    public void ClientsWithoutGroup_LeavesOutClientsWhoseNameMatchesAnUnlinkedGroup()
    {
        var groups = new[] { new GroupSummary("Compute Bridgend", null), new GroupSummary("McGarvey", null) };

        var suggestions = PsaCompanySuggestions.CompaniesWithoutGroup(PsaKind.HaloPsa, AllClients, groups);

        Assert.DoesNotContain(suggestions, client => client.Id == Compute.Id || client.Id == Mcgarvey.Id);
        Assert.Contains(suggestions, client => client.Id == TechPulse.Id);
    }

    [Fact]
    public void ClientsWithoutGroup_KeepsBothHarveysWhenTheGroupIsJustHarvey()
    {
        var groups = new[] { new GroupSummary("Harvey", null) };

        var suggestions = PsaCompanySuggestions.CompaniesWithoutGroup(PsaKind.HaloPsa, AllClients, groups);

        Assert.Contains(suggestions, client => client.Id == CherylHarvey.Id);
        Assert.Contains(suggestions, client => client.Id == LindaHarvey.Id);
    }

    [Fact]
    public void ClientsWithoutGroup_IsEmptyWhenEveryClientHasAGroup()
    {
        var groups = AllClients.Select(client => new GroupSummary(client.Name, client.Id));

        Assert.Empty(PsaCompanySuggestions.CompaniesWithoutGroup(PsaKind.HaloPsa, AllClients, groups));
    }

    [Fact]
    public void SuggestClientsForGroup_PutsTheExactMatchFirstThenClientsThatStartWithTheName()
    {
        var clients = new[] { new PsaCompany("50", "Acme Holdings"), new PsaCompany("51", "Acme") };

        var suggestions = PsaCompanySuggestions.SuggestCompaniesForGroup(PsaKind.HaloPsa, "Acme", clients);

        Assert.Equal(["51", "50"], suggestions.Select(client => client.Id));
    }

    [Fact]
    public void SuggestClientsForGroup_FindsAClientDespiteLimitedAndBrackets()
    {
        var suggestions = PsaCompanySuggestions.SuggestCompaniesForGroup(PsaKind.HaloPsa, "Compute Bridgend", AllClients);

        Assert.Equal([Compute.Id], suggestions.Select(client => client.Id));
    }

    [Fact]
    public void SuggestClientsForGroup_NeverSuggestsTheUnknownClient()
    {
        Assert.Empty(PsaCompanySuggestions.SuggestCompaniesForGroup(PsaKind.HaloPsa, "Unknown", AllClients));
    }

    [Fact]
    public void LooseKey_IgnoresCasePunctuationAndCompanySuffixes()
    {
        Assert.Equal("compute bridgend", PsaCompanySuggestions.LooseKey("Compute (Bridgend) Limited"));
        Assert.Equal(PsaCompanySuggestions.LooseKey("Contoso Ltd."), PsaCompanySuggestions.LooseKey("contoso"));
    }

    [Fact]
    public void HalosUnknownClient_IsOnlySkippedForHalo()
    {
        var companies = new[] { new PsaCompany("1", "Unknown"), new PsaCompany("2", "Fabrikam") };

        Assert.Equal(["Fabrikam"], PsaCompanySuggestions.CompaniesWithoutGroup(PsaKind.HaloPsa, companies, []).Select(company => company.Name));
        Assert.Equal(["Fabrikam", "Unknown"], PsaCompanySuggestions.CompaniesWithoutGroup(PsaKind.ConnectWise, companies, []).Select(company => company.Name));
    }
}
