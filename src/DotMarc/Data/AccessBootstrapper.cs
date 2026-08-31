using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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

    /// <summary>Canonical permission set for the built-in Viewer role, shared with
    /// DemoDataSeeder so the demo and production paths can't silently diverge if this list ever
    /// changes. The Admin role's permission list is NOT similarly shared: both places derive it
    /// identically via <c>[.. Enum.GetValues&lt;Permission&gt;()]</c>, which self-syncs when the
    /// enum grows, so there's no equivalent duplication risk there.</summary>
    public static readonly List<Permission> ViewerPermissions = [Permission.DomainsView, Permission.GroupsView, Permission.TagsView, Permission.AlertsView];

    public static async Task BootstrapWithLeaderLockAsync(DotMarcDbContext context, IOptions<InitialAdminsOptions> options, ILogger logger, CancellationToken cancellationToken = default)
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
            await EnsureBuiltInRoleAsync(context, "Viewer", isLocked: false, isScopable: true, ViewerPermissions, cancellationToken).ConfigureAwait(false);

            var anyAccessExists = await context.UserAccesses.AnyAsync(cancellationToken).ConfigureAwait(false);
            if (!anyAccessExists)
            {
                // Distinct (case-insensitively) before inserting: a duplicate or case-variant
                // entry in InitialAdmins:Emails (e.g. "a@x.com,A@X.com") would otherwise throw on
                // UserAccess's unique index on Email rather than being silently deduplicated —
                // crashing startup over what's obviously meant as one grant.
                var emails = options.Value.Emails
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                foreach (var email in emails)
                {
                    context.UserAccesses.Add(new UserAccess { Email = email, RoleId = adminRoleId });
                }
                if (emails.Length > 0)
                {
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    logger.LogInformation("Seeded {Count} initial admin grant(s) from InitialAdmins:Emails.", emails.Length);
                }
                else
                {
                    // The Critical-1-style lockout scenario: no existing grants AND nothing
                    // configured to seed. Every sign-in will be denied by the fallback policy
                    // until a grant is added, and only direct database access can add one at that
                    // point — this line is what makes that state self-diagnosing from the logs
                    // instead of a silent 403 with no explanation.
                    logger.LogWarning("No access grants exist and InitialAdmins:Emails is empty — every sign-in will be denied until an access grant is added, e.g. directly in the database.");
                }
            }
            else
            {
                logger.LogInformation("Skipped seeding initial admins — access grants already exist.");
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
