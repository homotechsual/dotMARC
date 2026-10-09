using DotMarc.Audit;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DotMarc.Data;

/// <summary>Grant/update/revoke operations for UserAccess rows, plus the sign-in-time
/// lookup/bind entry point (ResolveAsync) that DotMarc.Security.UserAccessClaimsTransformation
/// calls. Follows this project's DomainManagementService convention of a static class operating
/// directly on a caller-supplied DotMarcDbContext.</summary>
public static class UserAccessManagementService
{
    public enum GrantAccessResult { Granted, InvalidEmail, AlreadyExists, RoleNotFound }
    public enum UpdateAccessResult { Updated, RoleNotFound, ClientPortalNeedsGroups }
    public enum SetClientPortalResult { Updated, NeedsScopedGroups }
    public enum RevokeAccessResult { Revoked, LastAdminGuard }

    public static async Task<GrantAccessResult> GrantAccessAsync(DotMarcDbContext context, AuditActor actor, string rawEmail, int roleId, IReadOnlyList<int> groupIds, CancellationToken cancellationToken = default)
    {
        var email = rawEmail.Trim();
        if (string.IsNullOrEmpty(email))
        {
            return GrantAccessResult.InvalidEmail;
        }

        var role = await context.Roles.SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return GrantAccessResult.RoleNotFound;
        }

        var exists = await context.UserAccesses.AnyAsync(u => u.Email.ToLower() == email.ToLower(), cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return GrantAccessResult.AlreadyExists;
        }

        var groups = role.IsScopable
            ? await context.Groups.Where(g => groupIds.Contains(g.Id)).ToListAsync(cancellationToken).ConfigureAwait(false)
            : [];

        var access = new UserAccess { Email = email, RoleId = roleId, ScopedGroups = groups };
        context.UserAccesses.Add(access);

        try
        {
            await AuditLog.SaveAndRecordAsync(context,
                () => AuditLog.Record(context, actor, AuditActions.AccessGranted, AuditTarget.For(access), $"Granted {email} the {role.Name} role",
                    new AuditChanges()
                        .Field("Role", (string?)null, role.Name)
                        .Set("Groups", [], groups.Select(group => group.Name))),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return GrantAccessResult.AlreadyExists;
        }

        return GrantAccessResult.Granted;
    }

    /// <summary>Not yet called from any UI - ManageAccess.razor's page spec deliberately only
    /// covers Grant + Revoke, not editing an existing grant's role/scope. Reserved here for a
    /// future edit-grant feature; kept rather than removed so that feature doesn't have to
    /// re-derive this logic.</summary>
    public static async Task<UpdateAccessResult> UpdateAccessAsync(DotMarcDbContext context, AuditActor actor, int userAccessId, int roleId, IReadOnlyList<int> groupIds, CancellationToken cancellationToken = default)
    {
        var role = await context.Roles.SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return UpdateAccessResult.RoleNotFound;
        }

        var access = await context.UserAccesses.Include(u => u.Role).Include(u => u.ScopedGroups).AsSplitQuery()
            .SingleAsync(u => u.Id == userAccessId, cancellationToken).ConfigureAwait(false);
        var groups = role.IsScopable
            ? await context.Groups.Where(g => groupIds.Contains(g.Id)).ToListAsync(cancellationToken).ConfigureAwait(false)
            : [];
        if (access.IsClientPortal && groups.Count == 0)
        {
            return UpdateAccessResult.ClientPortalNeedsGroups;
        }

        var changes = new AuditChanges()
            .Field("Role", access.Role.Name, role.Name)
            .Set("Groups", access.ScopedGroups.Select(group => group.Name), groups.Select(group => group.Name));

        access.RoleId = roleId;
        access.ScopedGroups = groups;
        if (changes.Any)
        {
            AuditLog.Record(context, actor, AuditActions.AccessUpdated, AuditTarget.For(access), $"Changed access for {access.Email}", changes);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return UpdateAccessResult.Updated;
    }

    /// <summary>Turns the client portal on or off for a grant. A portal grant must be limited to Groups: with none it
    /// would see every domain, which is never what a client should get.</summary>
    public static async Task<SetClientPortalResult> SetClientPortalAsync(DotMarcDbContext context, AuditActor actor, int userAccessId, bool isClientPortal, CancellationToken cancellationToken = default)
    {
        var access = await context.UserAccesses.Include(u => u.Role).Include(u => u.ScopedGroups).AsSplitQuery()
            .SingleAsync(u => u.Id == userAccessId, cancellationToken).ConfigureAwait(false);
        if (isClientPortal && (!access.Role.IsScopable || access.ScopedGroups.Count == 0))
        {
            return SetClientPortalResult.NeedsScopedGroups;
        }

        var changes = new AuditChanges().Field("Client portal", access.IsClientPortal, isClientPortal);
        if (!changes.Any)
        {
            return SetClientPortalResult.Updated;
        }

        access.IsClientPortal = isClientPortal;
        AuditLog.Record(context, actor, AuditActions.AccessClientPortalChanged, AuditTarget.For(access),
            $"{(isClientPortal ? "Turned on" : "Turned off")} the client portal for {access.Email}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return SetClientPortalResult.Updated;
    }

    /// <summary>Refuses to revoke the last remaining grant that carries AccessManage - doing so
    /// would permanently lock the app out of its own Manage Access page, recoverable only via
    /// direct SQL. Checked by role/permission content, not by the built-in Admin role's identity,
    /// since a custom role could also carry AccessManage.</summary>
    public static async Task<RevokeAccessResult> RevokeAccessAsync(DotMarcDbContext context, AuditActor actor, int userAccessId, CancellationToken cancellationToken = default)
    {
        var access = await context.UserAccesses.Include(u => u.Role).SingleAsync(u => u.Id == userAccessId, cancellationToken).ConfigureAwait(false);

        if (access.Role.Permissions.Contains(Permission.AccessManage))
        {
            // Role.Permissions is a List<Permission> behind an EF Core value converter (see
            // DotMarcDbContext's ValueComparer comment) rather than a translatable primitive
            // collection, so a server-side .Contains() predicate on it isn't reliable. The access
            // grants table is small (staff plus a handful of external client contacts), so
            // pulling every other grant's role into memory for this one-off guard check is cheap
            // and avoids depending on unsupported LINQ translation.
            var otherGrants = await context.UserAccesses
                .Where(u => u.Id != userAccessId)
                .Include(u => u.Role)
                .AsNoTracking()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var anotherAdminExists = otherGrants.Any(u => u.Role.Permissions.Contains(Permission.AccessManage));
            if (!anotherAdminExists)
            {
                return RevokeAccessResult.LastAdminGuard;
            }
        }

        AuditLog.Record(context, actor, AuditActions.AccessRevoked, AuditTarget.For(access), $"Revoked access for {access.Email}");
        context.UserAccesses.Remove(access);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return RevokeAccessResult.Revoked;
    }

    /// <summary>Looks up the caller's access grant by Entra object ID first (the stable,
    /// already-bound case). Falling back to a case-insensitive email match only when no
    /// object-ID match is found - binding that grant's EntraObjectId to the given value so every
    /// later sign-in resolves by object ID instead. Returns null when neither matches: the caller
    /// (the claims transformation) simply adds no permission claims for an unrecognized
    /// identity, and the tightened fallback authorization policy denies them.</summary>
    public static async Task<UserAccess?> ResolveAsync(DotMarcDbContext context, string? entraObjectId, string? email, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(entraObjectId))
        {
            var bound = await context.UserAccesses
                .Include(u => u.Role)
                .Include(u => u.ScopedGroups)
                .SingleOrDefaultAsync(u => u.EntraObjectId == entraObjectId, cancellationToken)
                .ConfigureAwait(false);
            if (bound is not null)
            {
                return bound;
            }
        }

        if (string.IsNullOrEmpty(email))
        {
            return null;
        }

        var pending = await context.UserAccesses
            .Include(u => u.Role)
            .Include(u => u.ScopedGroups)
            .SingleOrDefaultAsync(u => u.EntraObjectId == null && u.Email.ToLower() == email.ToLower(), cancellationToken)
            .ConfigureAwait(false);
        if (pending is null || string.IsNullOrEmpty(entraObjectId))
        {
            return pending;
        }

        pending.EntraObjectId = entraObjectId;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return pending;
    }
}
