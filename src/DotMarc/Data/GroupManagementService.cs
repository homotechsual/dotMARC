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

    public static async Task<AddGroupResult> AddGroupAsync(DotMarcDbContext context, string rawName, CancellationToken cancellationToken = default)
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

        context.Groups.Add(new Group { Name = name });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // The unique index on Group.Name caught a same-cased race. A concurrent
            // different-cased duplicate (e.g. "Client A" vs "client a") is not caught by the
            // plain index — an accepted gap given group creation is a low-frequency manual
            // action, not the high-concurrency path Domain auto-discovery is.
            return AddGroupResult.AlreadyExists;
        }

        return AddGroupResult.Added;
    }

    public static async Task<AddGroupResult> RenameGroupAsync(DotMarcDbContext context, int groupId, string rawName, CancellationToken cancellationToken = default)
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

    /// <summary>Permanently deletes a Group row. DotMarcDbContext.cs's implicit many-to-many
    /// skip navigation between Domain and Group means EF removes the join rows via the join
    /// table's own cascade-delete foreign key — no Domain or Report data is touched.</summary>
    public static async Task RemoveGroupAsync(DotMarcDbContext context, int groupId, CancellationToken cancellationToken = default)
    {
        var group = await context.Groups.SingleAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        context.Groups.Remove(group);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces a domain's full set of group memberships with exactly the given group
    /// IDs — the multi-select on Manage Domains always submits the complete desired set, not an
    /// incremental add/remove.</summary>
    public static async Task SetDomainGroupsAsync(DotMarcDbContext context, int domainId, IReadOnlyList<int> groupIds, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.Include(d => d.Groups).SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        var groups = await context.Groups.Where(g => groupIds.Contains(g.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        domain.Groups = groups;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
