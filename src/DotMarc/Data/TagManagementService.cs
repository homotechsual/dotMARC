using Microsoft.EntityFrameworkCore;
using MudBlazor;
using Npgsql;

namespace DotMarc.Data;

/// <summary>Add/update/remove operations for Tag rows, created through the "Manage groups"
/// page, plus setting a domain's full tag membership from Manage Domains. Follows this
/// project's DomainManagementService convention of a static class operating directly on a
/// caller-supplied DotMarcDbContext.</summary>
public static class TagManagementService
{
    public enum AddTagResult { Added, InvalidName, AlreadyExists }

    public static async Task<AddTagResult> AddTagAsync(DotMarcDbContext context, string rawName, Color color, CancellationToken cancellationToken = default)
    {
        var name = rawName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return AddTagResult.InvalidName;
        }

        var exists = await context.Tags.AnyAsync(t => t.Name.ToLower() == name.ToLower(), cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return AddTagResult.AlreadyExists;
        }

        context.Tags.Add(new Tag { Name = name, Color = color });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return AddTagResult.AlreadyExists;
        }

        return AddTagResult.Added;
    }

    public static async Task<AddTagResult> UpdateTagAsync(DotMarcDbContext context, int tagId, string rawName, Color color, CancellationToken cancellationToken = default)
    {
        var name = rawName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return AddTagResult.InvalidName;
        }

        var exists = await context.Tags.AnyAsync(t => t.Id != tagId && t.Name.ToLower() == name.ToLower(), cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return AddTagResult.AlreadyExists;
        }

        var tag = await context.Tags.SingleAsync(t => t.Id == tagId, cancellationToken).ConfigureAwait(false);
        tag.Name = name;
        tag.Color = color;

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return AddTagResult.AlreadyExists;
        }

        return AddTagResult.Added;
    }

    /// <summary>Permanently deletes a Tag row. See GroupManagementService.RemoveGroupAsync's doc
    /// comment — the same implicit many-to-many cascade behavior applies here.</summary>
    public static async Task RemoveTagAsync(DotMarcDbContext context, int tagId, CancellationToken cancellationToken = default)
    {
        var tag = await context.Tags.SingleAsync(t => t.Id == tagId, cancellationToken).ConfigureAwait(false);
        context.Tags.Remove(tag);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces a domain's full set of tag memberships with exactly the given tag IDs —
    /// see GroupManagementService.SetDomainGroupsAsync's doc comment for why this replaces
    /// rather than incrementally adds/removes.</summary>
    public static async Task SetDomainTagsAsync(DotMarcDbContext context, int domainId, IReadOnlyList<int> tagIds, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.Include(d => d.Tags).SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        var tags = await context.Tags.Where(t => tagIds.Contains(t.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        domain.Tags = tags;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
