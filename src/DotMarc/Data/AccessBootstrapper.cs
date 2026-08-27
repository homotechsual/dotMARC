using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DotMarc.Data;

/// <summary>Ensures the built-in Admin/Viewer roles exist and, only when the UserAccess table is
/// completely empty, grants Admin to the emails configured via InitialAdmins:Emails. Guarded by
/// the same Postgres advisory-lock pattern as DatabaseMigrator, so multiple replicas starting
/// concurrently don't race each other. Called once at startup, right after migrations run and
/// before the app serves any request — see Program.cs — so there's no window where the
/// authorization fallback policy (tightened in a later task) is live before this has run.
/// "Empty UserAccess table" covers both a genuinely fresh deployment and this app's own existing
/// live deployment picking up the permissions feature for the first time: from the database's
/// perspective the two look identical, since there's no way to retroactively know who's been
/// using the app so far.</summary>
public static class AccessBootstrapper
{
    internal const long BootstrapLeaderLockKey = 84_200_004;

    public static async Task BootstrapWithLeaderLockAsync(DotMarcDbContext context, IOptions<InitialAdminsOptions> options, CancellationToken cancellationToken = default)
    {
        var connectionString = context.Database.GetConnectionString()
            ?? throw new InvalidOperationException("DotMarcDbContext has no connection string configured.");

        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var lockTransaction = await lockConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", BootstrapLeaderLockKey);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var adminRoleId = await EnsureBuiltInRoleAsync(context, "Admin", isLocked: true, isScopable: false, [.. Enum.GetValues<Permission>()], cancellationToken).ConfigureAwait(false);
            await EnsureBuiltInRoleAsync(context, "Viewer", isLocked: false, isScopable: true, [Permission.DomainsView, Permission.GroupsView, Permission.TagsView], cancellationToken).ConfigureAwait(false);

            var anyAccessExists = await context.UserAccesses.AnyAsync(cancellationToken).ConfigureAwait(false);
            if (!anyAccessExists)
            {
                var emails = options.Value.Emails.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var email in emails)
                {
                    context.UserAccesses.Add(new UserAccess { Email = email, RoleId = adminRoleId });
                }
                if (emails.Length > 0)
                {
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await lockTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<int> EnsureBuiltInRoleAsync(DotMarcDbContext context, string name, bool isLocked, bool isScopable, List<Permission> permissions, CancellationToken cancellationToken)
    {
        var existing = await context.Roles.SingleOrDefaultAsync(r => r.Name == name, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Id;
        }

        var role = new Role { Name = name, IsLocked = isLocked, IsScopable = isScopable, Permissions = permissions };
        context.Roles.Add(role);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return role.Id;
    }
}
