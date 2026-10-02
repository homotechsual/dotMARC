using System.Globalization;
using DotMarc.Data;
using DotMarc.Notifications;

namespace DotMarc.Audit;

/// <summary>What an audit entry is about, with its name as it was at the time.</summary>
public sealed record AuditTarget(string Type, string? Id, string? Name)
{
    public static AuditTarget For(Domain domain) => new("Domain", IdText(domain.Id), domain.Name);
    public static AuditTarget For(Group group) => new("Group", IdText(group.Id), group.Name);
    public static AuditTarget For(Tag tag) => new("Tag", IdText(tag.Id), tag.Name);
    public static AuditTarget For(Role role) => new("Role", IdText(role.Id), role.Name);
    public static AuditTarget For(UserAccess access) => new("UserAccess", IdText(access.Id), access.Email);
    public static AuditTarget For(AlertEvent alert) => new("Alert", IdText(alert.Id), alert.DomainName);

    /// <summary>A settings screen, such as "HaloPSA" or "Notifications". There is only one of each, so no id.</summary>
    public static AuditTarget Settings(string name) => new("Settings", null, name);

    /// <summary>A domain known only by name, as on a page view of /domains/{name}.</summary>
    public static AuditTarget DomainNamed(string domainName) => new("Domain", null, domainName);

    private static string IdText(int id) => id.ToString(CultureInfo.InvariantCulture);
}
