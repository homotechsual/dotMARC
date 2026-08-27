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

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23001" or "23503" })
        {
            // A UserAccess grant was inserted against this role in the window between the
            // AnyAsync check above and this SaveChangesAsync (READ COMMITTED, no explicit
            // locking) — the FK's DeleteBehavior.Restrict caught it. Report the same InUse
            // result the upfront check would have given, rather than letting the FK violation
            // leak out as an unhandled exception.
            //
            // SqlState is 23001 (restrict_violation), not 23503 (foreign_key_violation), because
            // this FK is configured with DeleteBehavior.Restrict, which Npgsql's migrations
            // generator emits as an explicit "ON DELETE RESTRICT" clause rather than the
            // clauseless default (ON DELETE NO ACTION) that raises 23503 — confirmed by actually
            // triggering this catch block against a real Postgres container
            // (RemoveRoleAsync_ReturnsInUse_WhenAGrantIsInsertedBetweenTheCheckAndTheDelete).
            // 23503 is kept alongside it as a defensive fallback in case that mapping ever
            // changes.
            return RemoveRoleResult.InUse;
        }

        return RemoveRoleResult.Removed;
    }
}
