namespace DotMarc.Data;

/// <summary>A named bundle of Permissions, grantable to any number of people via UserAccess.
/// IsLocked is true only for the built-in Admin role — enforced in RoleManagementService, not
/// just hidden in the UI, so Admin can never be renamed, have its permissions changed, or be
/// deleted through any code path, keeping it a reliable break-glass account. IsScopable is true
/// only for the built-in Viewer role and is never exposed as something an admin can set on a
/// custom role — it exists so "scope only applies to Viewer" survives a rename of Viewer itself,
/// rather than being a fragile string comparison against the name "Viewer".</summary>
public sealed class Role
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool IsLocked { get; set; }
    public bool IsScopable { get; set; }
    public List<Permission> Permissions { get; set; } = [];
}
