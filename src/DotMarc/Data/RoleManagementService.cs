using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DotMarc.Data;

/// <summary>Add/update/remove operations for Role rows, created through the "Manage access"
/// page. Follows this project's DomainManagementService convention of a static class operating
/// directly on a caller-supplied DotMarcDbContext.</summary>
public static class RoleManagementService
{
    public enum AddRoleResult { Added, InvalidName, AlreadyExists }
    public enum UpdateRoleResult { Updated, InvalidName, AlreadyExists, Locked }
    public enum RemoveRoleResult { Removed, Locked, InUse }

    public static async Task<AddRoleResult> AddRoleAsync(DotMarcDbContext context, string rawName, List<Permission> permissions, CancellationToken cancellationToken = default)
    {
        var name = rawName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return AddRoleResult.InvalidName;
        }

        var exists = await context.Roles.AnyAsync(r => r.Name.ToLower() == name.ToLower(), cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return AddRoleResult.AlreadyExists;
        }

        context.Roles.Add(new Role { Name = name, IsLocked = false, IsScopable = false, Permissions = permissions });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return AddRoleResult.AlreadyExists;
        }

        return AddRoleResult.Added;
    }

    public static async Task<UpdateRoleResult> UpdateRoleAsync(DotMarcDbContext context, int roleId, string rawName, List<Permission> permissions, CancellationToken cancellationToken = default)
    {
        var role = await context.Roles.SingleAsync(r => r.Id == roleId, cancellationToken).ConfigureAwait(false);
        if (role.IsLocked)
        {
            return UpdateRoleResult.Locked;
        }

        var name = rawName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return UpdateRoleResult.InvalidName;
        }

        var exists = await context.Roles.AnyAsync(r => r.Id != roleId && r.Name.ToLower() == name.ToLower(), cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return UpdateRoleResult.AlreadyExists;
        }

        role.Name = name;
        role.Permissions = permissions;

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return UpdateRoleResult.AlreadyExists;
        }

        return UpdateRoleResult.Updated;
    }

    /// <summary>Unlike Group/Tag deletion (which only ever removes membership rows and is always
    /// safe), deleting a Role that's still granted to someone would leave their UserAccess row
    /// pointing at nothing — an undefined-permissions state. This checks first and refuses rather
    /// than letting that happen; the database's own DeleteBehavior.Restrict foreign key is a
    /// backstop behind this check, not the primary guard.</summary>
    public static async Task<RemoveRoleResult> RemoveRoleAsync(DotMarcDbContext context, int roleId, CancellationToken cancellationToken = default)
    {
        var role = await context.Roles.SingleAsync(r => r.Id == roleId, cancellationToken).ConfigureAwait(false);
        if (role.IsLocked)
        {
            return RemoveRoleResult.Locked;
        }

        var inUse = await context.UserAccesses.AnyAsync(u => u.RoleId == roleId, cancellationToken).ConfigureAwait(false);
        if (inUse)
        {
            return RemoveRoleResult.InUse;
        }

        context.Roles.Remove(role);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return RemoveRoleResult.Removed;
    }
}
