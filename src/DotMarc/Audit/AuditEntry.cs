namespace DotMarc.Audit;

public enum AuditEntryKind { Change, SignIn, PageView }

/// <summary>Who did something. <c>ApiKey</c> joins this when the public API is built.</summary>
public enum AuditActorKind { User, System }

/// <summary>One changed field in an audit entry. For a secret, <see cref="Old"/> and <see cref="New"/> are null and
/// <see cref="Secret"/> is true, so the value itself is never stored.</summary>
public sealed record AuditFieldChange(string Field, string? Old, string? New, bool Secret = false);

/// <summary>One thing that happened: a change someone made, a sign-in, or a page someone opened. Entries are never
/// edited, and leave only through retention (<c>AuditRetention</c>) or the demo reset.</summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset OccurredUtc { get; set; }
    public AuditEntryKind Kind { get; set; }
    public AuditActorKind ActorKind { get; set; }
    public string? ActorObjectId { get; set; }

    /// <summary>The email as it was at the time, so the entry stays readable after the grant is revoked.</summary>
    public string? ActorEmail { get; set; }

    public string ActorName { get; set; } = "";

    /// <summary>A stable code from <c>AuditActions</c>.</summary>
    public string Action { get; set; } = "";

    public string? TargetType { get; set; }
    public string? TargetId { get; set; }

    /// <summary>The target's display name at the time, for example the domain name.</summary>
    public string? TargetName { get; set; }

    public string Summary { get; set; } = "";
    public List<AuditFieldChange> Changes { get; set; } = [];
}
