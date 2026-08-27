namespace DotMarc.Data;

/// <summary>Every independently-grantable capability in the app. A fixed, closed set — adding a
/// new one is a code change (a new UI surface to gate), not something an admin can define, so this
/// is an enum rather than a database-driven list.</summary>
public enum Permission
{
    DomainsView,
    DomainsAdd,
    DomainsEdit,
    DomainsReorder,
    DomainsDelete,
    GroupsView,
    GroupsAdd,
    GroupsRename,
    GroupsDelete,
    TagsView,
    TagsAdd,
    TagsEdit,
    TagsDelete,
    AccessManage
}
