namespace DotMarc.Data;

/// <summary>One granted person — internal staff or an external client contact, granted the same
/// way. Email is what an admin types and is authoritative until EntraObjectId is populated on
/// that email's first successful sign-in, after which lookups use the object ID so a later
/// UPN/email rename on the Entra side can't orphan the grant. ScopedGroups only has any effect
/// when Role.IsScopable is true (see Role's doc comment) — UserAccessManagementService clears it
/// for any other role. An empty ScopedGroups list on a scopable role's grant means unrestricted
/// view access, not "access to nothing" — matching how the Dashboard's own Group filter already
/// treats "no filter selected".</summary>
public sealed class UserAccess
{
    public int Id { get; set; }
    public required string Email { get; set; }
    public string? EntraObjectId { get; set; }
    public int RoleId { get; set; }
    public Role Role { get; set; } = null!;
    public List<Group> ScopedGroups { get; set; } = [];
}
