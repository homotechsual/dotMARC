using System.Security.Claims;
using DotMarc.Audit;
using Xunit;

namespace DotMarc.Tests.Audit;

public sealed class AuditActorTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Test", nameType: ClaimTypes.Name, roleType: ClaimTypes.Role));

    [Fact]
    public void FromPrincipal_UsesTheNameEmailAndObjectId()
    {
        var actor = AuditActor.FromPrincipal(Principal(
            new Claim("oid", "0f1e2d3c-0000-0000-0000-000000000001"),
            new Claim("preferred_username", "sam@contoso.com"),
            new Claim(ClaimTypes.Name, "Sam Jones")));

        Assert.Equal(new AuditActor(AuditActorKind.User, "Sam Jones", "0f1e2d3c-0000-0000-0000-000000000001", "sam@contoso.com"), actor);
    }

    [Fact]
    public void FromPrincipal_FallsBackToTheEmail_WhenThereIsNoName()
    {
        var actor = AuditActor.FromPrincipal(Principal(new Claim("preferred_username", "sam@contoso.com")));

        Assert.Equal("sam@contoso.com", actor.Name);
    }

    [Fact]
    public void FromPrincipal_WithNoIdentityClaims_IsAnUnknownUser()
    {
        var actor = AuditActor.FromPrincipal(new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.Equal(new AuditActor(AuditActorKind.User, "Unknown user"), actor);
    }

    [Fact]
    public void ForSystem_NamesTheSystemActor()
    {
        Assert.Equal(new AuditActor(AuditActorKind.System, "Startup"), AuditActor.ForSystem("Startup"));
    }
}
