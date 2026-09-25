// test/DotMarc.Tests/Notifications/HaloOptionNamesTests.cs
using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class HaloOptionNamesTests
{
    private static readonly HaloTicketType[] TicketTypes = [new(23, "RMM Alert"), new(5, "Incident")];
    private static readonly HaloPriority[] Priorities = [new(1, "Urgent"), new(4, "Low")];
    private static readonly HaloTicketStatus[] Statuses = [new(1, "New"), new(9, "Closed")];
    private static readonly HaloAgent[] Agents = [new(3, "Mikey O'Toole")];

    [Fact]
    public void Remember_TakesTheNamesOfTheSelectedOptionsFromTheLoadedLists()
    {
        var settings = new HaloPsaSettings { TicketTypeId = 23, DefaultPriorityId = 4, ClosedStatusId = 9, AssignedAgentId = 3 };

        HaloOptionNames.Remember(settings, TicketTypes, Priorities, Statuses, Agents);

        Assert.Equal("RMM Alert", settings.TicketTypeName);
        Assert.Equal("Low", settings.DefaultPriorityName);
        Assert.Equal("Closed", settings.ClosedStatusName);
        Assert.Equal("Mikey O'Toole", settings.AssignedAgentName);
    }

    [Fact]
    public void Remember_KeepsTheSavedNames_WhenTheListsHaveNotBeenLoaded()
    {
        // Saving without loading Halo's lists must not throw the names away and leave bare numbers.
        var settings = new HaloPsaSettings
        {
            TicketTypeId = 23, TicketTypeName = "RMM Alert",
            DefaultPriorityId = 4, DefaultPriorityName = "Low",
            ClosedStatusId = 9, ClosedStatusName = "Closed",
            AssignedAgentId = 3, AssignedAgentName = "Mikey O'Toole"
        };

        HaloOptionNames.Remember(settings, [], [], [], []);

        Assert.Equal("RMM Alert", settings.TicketTypeName);
        Assert.Equal("Low", settings.DefaultPriorityName);
        Assert.Equal("Closed", settings.ClosedStatusName);
        Assert.Equal("Mikey O'Toole", settings.AssignedAgentName);
    }

    [Fact]
    public void Remember_UsesTheNameFromTheList_WhenHaloHasRenamedTheOption()
    {
        var settings = new HaloPsaSettings { ClosedStatusId = 9, ClosedStatusName = "Closed (old name)" };

        HaloOptionNames.Remember(settings, [], [], Statuses, []);

        Assert.Equal("Closed", settings.ClosedStatusName);
    }

    [Fact]
    public void Remember_ClearsTheName_WhenNothingIsSelected()
    {
        // "Don't assign" is a null id, and a stale agent name must not linger behind it.
        var settings = new HaloPsaSettings { AssignedAgentId = null, AssignedAgentName = "Mikey O'Toole" };

        HaloOptionNames.Remember(settings, TicketTypes, Priorities, Statuses, Agents);

        Assert.Null(settings.AssignedAgentName);
    }

    [Fact]
    public void Remember_TakesTheNewName_WhenADifferentOptionIsChosen()
    {
        var settings = new HaloPsaSettings { TicketTypeId = 5, TicketTypeName = "RMM Alert" };

        HaloOptionNames.Remember(settings, TicketTypes, [], [], []);

        Assert.Equal("Incident", settings.TicketTypeName);
    }

    [Fact]
    public void WithSaved_OffersTheSavedSelectionByName_WhenNothingHasBeenLoaded()
    {
        var options = HaloOptionNames.WithSaved([], 23, "RMM Alert");

        Assert.Equal([(23, "RMM Alert")], options);
    }

    [Fact]
    public void WithSaved_DoesNotDuplicateTheSelection_OnceTheListsAreLoaded()
    {
        var options = HaloOptionNames.WithSaved(TicketTypes.Select(option => (option.Id, option.Name)), 23, "RMM Alert");

        Assert.Equal(2, options.Count);
        Assert.Single(options, option => option.Id == 23);
    }

    [Theory]
    [InlineData(null, "RMM Alert")]
    [InlineData(23, null)]
    [InlineData(23, "  ")]
    public void WithSaved_AddsNothing_WithoutBothAnIdAndAName(int? savedId, string? savedName)
    {
        Assert.Empty(HaloOptionNames.WithSaved([], savedId, savedName));
    }
}
