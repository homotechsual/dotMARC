using DotMarc.Audit;
using DotMarc.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DotMarc.Data;

public enum CreateApiKeyError { InvalidName, NameInUse, RoleNotFound, RoleCanManageAccess, InvalidLifetime, GroupNotFound }

public enum RevokeApiKeyResult { Revoked, NotFound, AlreadyRevoked }

/// <summary>The new key and its secret, which is never available again; or why it wasn't created.</summary>
public sealed record CreateApiKeyResult(ApiKey? Key, string? Secret, CreateApiKeyError? Error)
{
    public static CreateApiKeyResult Refused(CreateApiKeyError error) => new(null, null, error);
}

/// <summary>Creates, revokes and lists API keys from the Access page. Follows the project's convention of a static
/// class working on a caller-supplied context, with every change audited.</summary>
public static class ApiKeyManagementService
{
    public const int MaximumNameLength = 100;
    public static readonly int[] LifetimeDays = [30, 90, 180, 365];

    public static async Task<CreateApiKeyResult> CreateAsync(DotMarcDbContext context, AuditActor actor, string rawName, int roleId,
        IReadOnlyList<int> groupIds, int lifetimeDays, CancellationToken cancellationToken = default)
    {
        var name = rawName.Trim();
        if (name.Length is 0 or > MaximumNameLength)
        {
            return CreateApiKeyResult.Refused(CreateApiKeyError.InvalidName);
        }

        if (!LifetimeDays.Contains(lifetimeDays))
        {
            return CreateApiKeyResult.Refused(CreateApiKeyError.InvalidLifetime);
        }

        var role = await context.Roles.SingleOrDefaultAsync(candidate => candidate.Id == roleId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return CreateApiKeyResult.Refused(CreateApiKeyError.RoleNotFound);
        }

        // Keys can't mint keys or change who has access.
        if (role.Permissions.Contains(Permission.AccessManage))
        {
            return CreateApiKeyResult.Refused(CreateApiKeyError.RoleCanManageAccess);
        }

        var loweredName = name.ToLower();
        if (await context.ApiKeys.AnyAsync(key => key.RevokedUtc == null && key.Name.ToLower() == loweredName, cancellationToken).ConfigureAwait(false))
        {
            return CreateApiKeyResult.Refused(CreateApiKeyError.NameInUse);
        }

        var groups = role.IsScopable
            ? await context.Groups.Where(group => groupIds.Contains(group.Id)).ToListAsync(cancellationToken).ConfigureAwait(false)
            : [];

        // A key with no groups sees every group, so a group deleted while the form was open must not quietly widen it.
        if (role.IsScopable && groups.Count != groupIds.Distinct().Count())
        {
            return CreateApiKeyResult.Refused(CreateApiKeyError.GroupNotFound);
        }
        var secret = ApiKeySecrets.Generate();
        var nowUtc = DateTimeOffset.UtcNow;
        var apiKey = new ApiKey
        {
            Name = name,
            Prefix = ApiKeySecrets.DisplayPrefix(secret),
            Hash = ApiKeySecrets.Hash(secret),
            RoleId = role.Id,
            ScopedGroups = groups,
            CreatedBy = actor.Name,
            CreatedUtc = nowUtc,
            ExpiresUtc = nowUtc.AddDays(lifetimeDays),
        };
        context.ApiKeys.Add(apiKey);

        try
        {
            await AuditLog.SaveAndRecordAsync(context,
                () => AuditLog.Record(context, actor, AuditActions.ApiKeyCreated, AuditTarget.For(apiKey), $"Created API key {name} with the {role.Name} role",
                    new AuditChanges()
                        .Field("Role", (string?)null, role.Name)
                        .Set("Groups", [], groups.Select(group => group.Name))
                        .Field("Expires", (string?)null, apiKey.ExpiresUtc.ToString("yyyy-MM-dd"))),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            // Another key with this name was created between the check above and this save.
            return CreateApiKeyResult.Refused(CreateApiKeyError.NameInUse);
        }

        return new CreateApiKeyResult(apiKey, secret, null);
    }

    public static async Task<RevokeApiKeyResult> RevokeAsync(DotMarcDbContext context, AuditActor actor, int keyId, CancellationToken cancellationToken = default)
    {
        var apiKey = await context.ApiKeys.SingleOrDefaultAsync(key => key.Id == keyId, cancellationToken).ConfigureAwait(false);
        if (apiKey is null)
        {
            return RevokeApiKeyResult.NotFound;
        }

        if (apiKey.RevokedUtc is not null)
        {
            return RevokeApiKeyResult.AlreadyRevoked;
        }

        apiKey.RevokedUtc = DateTimeOffset.UtcNow;
        apiKey.RevokedBy = actor.Name;
        AuditLog.Record(context, actor, AuditActions.ApiKeyRevoked, AuditTarget.For(apiKey), $"Revoked API key {apiKey.Name}");
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return RevokeApiKeyResult.Revoked;
    }

    /// <summary>Keys in use first, then revoked ones, each by name.</summary>
    public static Task<List<ApiKey>> ListAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.ApiKeys
            .AsNoTracking()
            .Include(key => key.Role)
            .Include(key => key.ScopedGroups)
            .OrderBy(key => key.RevokedUtc != null)
            .ThenBy(key => key.Name)
            .ToListAsync(cancellationToken);
}
