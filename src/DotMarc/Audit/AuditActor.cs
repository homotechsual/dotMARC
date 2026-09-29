using System.Security.Claims;
using DotMarc.Security;
using Microsoft.Identity.Web;

namespace DotMarc.Audit;

/// <summary>Who an audit entry is about. Captured at the time, so an entry stays readable after the person's
/// grant is revoked or their name changes.</summary>
public sealed record AuditActor(AuditActorKind Kind, string Name, string? ObjectId = null, string? Email = null)
{
    /// <summary>Work nobody clicked, such as "Startup" seeding the initial admins.</summary>
    public static AuditActor ForSystem(string name) => new(AuditActorKind.System, name);

    public static AuditActor ForUser(string? objectId, string? email, string? displayName) =>
        new(AuditActorKind.User,
            FirstNonEmpty(displayName, email, objectId) ?? "Unknown user",
            string.IsNullOrEmpty(objectId) ? null : objectId,
            string.IsNullOrEmpty(email) ? null : email);

    public static AuditActor FromPrincipal(ClaimsPrincipal principal) =>
        ForUser(principal.GetObjectId(), UserClaims.GetEmail(principal), principal.Identity?.Name);

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate => !string.IsNullOrEmpty(candidate));
}
