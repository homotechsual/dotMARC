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
    public enum UpdateAccessResult { Updated, RoleNotFound }

    public static async Task<GrantAccessResult> GrantAccessAsync(DotMarcDbContext context, string rawEmail, int roleId, IReadOnlyList<int> groupIds, CancellationToken cancellationToken = default)
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

        context.UserAccesses.Add(new UserAccess { Email = email, RoleId = roleId, ScopedGroups = groups });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return GrantAccessResult.AlreadyExists;
        }

        return GrantAccessResult.Granted;
    }

    public static async Task<UpdateAccessResult> UpdateAccessAsync(DotMarcDbContext context, int userAccessId, int roleId, IReadOnlyList<int> groupIds, CancellationToken cancellationToken = default)
    {
        var role = await context.Roles.SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return UpdateAccessResult.RoleNotFound;
        }

        var access = await context.UserAccesses.Include(u => u.ScopedGroups).SingleAsync(u => u.Id == userAccessId, cancellationToken).ConfigureAwait(false);
        access.RoleId = roleId;
        access.ScopedGroups = role.IsScopable
            ? await context.Groups.Where(g => groupIds.Contains(g.Id)).ToListAsync(cancellationToken).ConfigureAwait(false)
            : [];

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return UpdateAccessResult.Updated;
    }

    public static async Task RevokeAccessAsync(DotMarcDbContext context, int userAccessId, CancellationToken cancellationToken = default)
    {
        var access = await context.UserAccesses.SingleAsync(u => u.Id == userAccessId, cancellationToken).ConfigureAwait(false);
        context.UserAccesses.Remove(access);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Looks up the caller's access grant by Entra object ID first (the stable,
    /// already-bound case). Falling back to a case-insensitive email match only when no
    /// object-ID match is found — binding that grant's EntraObjectId to the given value so every
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
