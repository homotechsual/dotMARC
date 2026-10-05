using DotMarc.Audit;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DotMarc.Data;

/// <summary>Add/rename/remove operations for Group rows, created through the "Manage groups"
/// page, plus setting a domain's full group membership from Manage Domains. Follows this
/// project's DomainManagementService convention of a static class operating directly on a
/// caller-supplied DotMarcDbContext.</summary>
public static class GroupManagementService
{
    public enum AddGroupResult { Added, InvalidName, AlreadyExists }

    /// <param name="haloClientId">Links the new group to this Halo client straight away, as when a group is created
    /// from a Halo client on Manage groups.</param>
    public static async Task<AddGroupResult> AddGroupAsync(DotMarcDbContext context, AuditActor actor, string rawName, CancellationToken cancellationToken = default, int? haloClientId = null)
    {
        var name = rawName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return AddGroupResult.InvalidName;
        }

        var exists = await context.Groups.AnyAsync(g => g.Name.ToLower() == name.ToLower(), cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return AddGroupResult.AlreadyExists;
        }

        var group = new Group { Name = name, HaloClientId = haloClientId };
        context.Groups.Add(group);

        try
        {
            await AuditLog.SaveAndRecordAsync(context,
                () => AuditLog.Record(context, actor, AuditActions.GroupAdded, AuditTarget.For(group), $"Added group {group.Name}",
                    new AuditChanges().Field("Halo client", (int?)null, haloClientId)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // The unique index on Group.Name caught a same-cased race. A concurrent
            // different-cased duplicate (e.g. "Client A" vs "client a") is not caught by the
            // plain index - an accepted gap given group creation is a low-frequency manual
            // action, not the high-concurrency path Domain auto-discovery is.
            return AddGroupResult.AlreadyExists;
        }

        return AddGroupResult.Added;
    }

    public static async Task<AddGroupResult> RenameGroupAsync(DotMarcDbContext context, AuditActor actor, int groupId, string rawName, CancellationToken cancellationToken = default)
    {
        var name = rawName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return AddGroupResult.InvalidName;
        }

        var exists = await context.Groups.AnyAsync(g => g.Id != groupId && g.Name.ToLower() == name.ToLower(), cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return AddGroupResult.AlreadyExists;
        }

        var group = await context.Groups.SingleAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Field("Name", group.Name, name);
        if (!changes.Any)
        {
            return AddGroupResult.Added;
        }

        AuditLog.Record(context, actor, AuditActions.GroupRenamed, AuditTarget.For(group), $"Renamed group {group.Name} to {name}", changes);
        group.Name = name;

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return AddGroupResult.AlreadyExists;
        }

        return AddGroupResult.Added;
    }

    public enum RemoveGroupResult { Removed, InUseByApiKey }

    /// <summary>Permanently deletes a Group row. DotMarcDbContext.cs's implicit many-to-many
    /// skip navigation between Domain and Group means EF removes the join rows via the join
    /// table's own cascade-delete foreign key - no Domain or Report data is touched. Refused while
    /// an unrevoked API key is limited to the group: a key left with no groups would see every
    /// group, and a key can't be edited, so it has to be revoked first.</summary>
    public static async Task<RemoveGroupResult> RemoveGroupAsync(DotMarcDbContext context, AuditActor actor, int groupId, CancellationToken cancellationToken = default)
    {
        var group = await context.Groups.SingleAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        var limitsAnApiKey = await context.ApiKeys
            .AnyAsync(key => key.RevokedUtc == null && key.ScopedGroups.Any(scopedGroup => scopedGroup.Id == groupId), cancellationToken)
            .ConfigureAwait(false);
        if (limitsAnApiKey)
        {
            return RemoveGroupResult.InUseByApiKey;
        }

        AuditLog.Record(context, actor, AuditActions.GroupRemoved, AuditTarget.For(group), $"Removed group {group.Name}");
        context.Groups.Remove(group);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return RemoveGroupResult.Removed;
    }

    /// <summary>Replaces a domain's full set of group memberships with exactly the given group
    /// IDs - the multi-select on Manage Domains always submits the complete desired set, not an
    /// incremental add/remove.</summary>
    public static async Task SetDomainGroupsAsync(DotMarcDbContext context, AuditActor actor, int domainId, IReadOnlyList<int> groupIds, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.Include(d => d.Groups).SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        var groups = await context.Groups.Where(g => groupIds.Contains(g.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Set("Groups", domain.Groups.Select(group => group.Name), groups.Select(group => group.Name));
        if (!changes.Any)
        {
            return;
        }

        domain.Groups = groups;
        AuditLog.Record(context, actor, AuditActions.DomainGroupsChanged, AuditTarget.For(domain), $"Changed the groups for {domain.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets (or clears, with null) a Group's Halo client mapping, from the "Halo Client"
    /// column on Manage Groups.</summary>
    public static async Task SetHaloClientIdAsync(DotMarcDbContext context, AuditActor actor, int groupId, int? haloClientId, CancellationToken cancellationToken = default)
    {
        var group = await context.Groups.SingleAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Field("Halo client", group.HaloClientId, haloClientId);
        if (!changes.Any)
        {
            return;
        }

        group.HaloClientId = haloClientId;
        AuditLog.Record(context, actor, AuditActions.GroupHaloClientChanged, AuditTarget.For(group), $"Changed the Halo client for group {group.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
