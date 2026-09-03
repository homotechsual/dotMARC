using DotMarc.Data;
using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class HaloClientResolverTests
{
    [Fact]
    public void Resolve_ReturnsTheDomainOverride_WhenSet()
    {
        var domain = new Domain { Name = "contoso.io", HaloClientId = 99, Groups = [new Group { Id = 1, Name = "g", HaloClientId = 1 }] };

        Assert.Equal(99, HaloClientResolver.Resolve(domain));
    }

    [Fact]
    public void Resolve_ReturnsTheLowestIdGroupWithAMapping_WhenNoOverride()
    {
        var domain = new Domain
        {
            Name = "contoso.io",
            Groups =
            [
                new Group { Id = 5, Name = "later", HaloClientId = 50 },
                new Group { Id = 2, Name = "earlier", HaloClientId = 20 },
                new Group { Id = 3, Name = "unmapped", HaloClientId = null }
            ]
        };

        Assert.Equal(20, HaloClientResolver.Resolve(domain));
    }

    [Fact]
    public void Resolve_SkipsGroupsWithNoMapping_EvenIfTheyHaveTheLowestId()
    {
        var domain = new Domain
        {
            Name = "contoso.io",
            Groups =
            [
                new Group { Id = 1, Name = "unmapped", HaloClientId = null },
                new Group { Id = 2, Name = "mapped", HaloClientId = 42 }
            ]
        };

        Assert.Equal(42, HaloClientResolver.Resolve(domain));
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenNothingIsMapped()
    {
        var domain = new Domain { Name = "contoso.io", Groups = [new Group { Id = 1, Name = "g" }] };

        Assert.Null(HaloClientResolver.Resolve(domain));
    }
}
