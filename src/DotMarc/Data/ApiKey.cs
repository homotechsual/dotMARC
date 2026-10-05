namespace DotMarc.Data;

/// <summary>A key that lets a tool call the public API with a role's permissions, optionally limited to some groups
/// when the role is scopable. Only a hash of the secret is stored; the secret itself is shown once, when the key is
/// created. A key is never edited: changing what it can do means creating a new key and revoking this one, so its
/// power never silently grows. RoleId becomes null only when a role is deleted after every key using it was revoked.</summary>
public sealed class ApiKey
{
    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>The first 12 characters of the secret ("dmk_" and 8 more), shown in lists to tell keys apart.</summary>
    public required string Prefix { get; set; }

    /// <summary>SHA-256 of the full secret, as lowercase hex.</summary>
    public required string Hash { get; set; }

    public int? RoleId { get; set; }
    public Role? Role { get; set; }
    public List<Group> ScopedGroups { get; set; } = [];

    /// <summary>The creator's display name at the time, so the key stays attributable after their grant goes.</summary>
    public required string CreatedBy { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public DateTimeOffset? LastUsedUtc { get; set; }
    public DateTimeOffset? RevokedUtc { get; set; }
    public string? RevokedBy { get; set; }

    public bool IsActive(DateTimeOffset nowUtc) => RevokedUtc is null && ExpiresUtc > nowUtc;
}
