using DotMarc.Audit;
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
    public enum AddTagResult { Added, InvalidName, AlreadyExists, InvalidColor }

    /// <summary>The only tag colors this project's design allows. Success/Warning/Error are
    /// deliberately excluded - they already carry pass/fail/status meaning on the Dashboard's
    /// Report Status and DNS Status chips, so a tag using one of them would visually read as a
    /// status indicator. This is the single canonical source of truth; ManageGroups.razor's
    /// color picker reads from here rather than duplicating the list.</summary>
    public static readonly IReadOnlyList<Color> AllowedColors = [Color.Primary, Color.Secondary, Color.Tertiary, Color.Info, Color.Dark];

    public static async Task<AddTagResult> AddTagAsync(DotMarcDbContext context, AuditActor actor, string rawName, Color color, CancellationToken cancellationToken = default)
    {
        var name = rawName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return AddTagResult.InvalidName;
        }

        if (!AllowedColors.Contains(color))
        {
            return AddTagResult.InvalidColor;
        }

        var exists = await context.Tags.AnyAsync(t => t.Name.ToLower() == name.ToLower(), cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return AddTagResult.AlreadyExists;
        }

        var tag = new Tag { Name = name, Color = color };
        context.Tags.Add(tag);

        try
        {
            await AuditLog.SaveAndRecordAsync(context,
                () => AuditLog.Record(context, actor, AuditActions.TagAdded, AuditTarget.For(tag), $"Added tag {tag.Name}",
                    new AuditChanges().Field("Color", (Color?)null, color)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return AddTagResult.AlreadyExists;
        }

        return AddTagResult.Added;
    }

    public static async Task<AddTagResult> UpdateTagAsync(DotMarcDbContext context, AuditActor actor, int tagId, string rawName, Color color, CancellationToken cancellationToken = default)
    {
        var name = rawName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return AddTagResult.InvalidName;
        }

        if (!AllowedColors.Contains(color))
        {
            return AddTagResult.InvalidColor;
        }

        var exists = await context.Tags.AnyAsync(t => t.Id != tagId && t.Name.ToLower() == name.ToLower(), cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return AddTagResult.AlreadyExists;
        }

        var tag = await context.Tags.SingleAsync(t => t.Id == tagId, cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Field("Name", tag.Name, name).Field("Color", tag.Color, color);
        if (!changes.Any)
        {
            return AddTagResult.Added;
        }

        AuditLog.Record(context, actor, AuditActions.TagUpdated, AuditTarget.For(tag), $"Updated tag {name}", changes);
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
    /// comment - the same implicit many-to-many cascade behavior applies here.</summary>
    public static async Task RemoveTagAsync(DotMarcDbContext context, AuditActor actor, int tagId, CancellationToken cancellationToken = default)
    {
        var tag = await context.Tags.SingleAsync(t => t.Id == tagId, cancellationToken).ConfigureAwait(false);
        AuditLog.Record(context, actor, AuditActions.TagRemoved, AuditTarget.For(tag), $"Removed tag {tag.Name}");
        context.Tags.Remove(tag);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces a domain's full set of tag memberships with exactly the given tag IDs - 
    /// see GroupManagementService.SetDomainGroupsAsync's doc comment for why this replaces
    /// rather than incrementally adds/removes.</summary>
    public static async Task SetDomainTagsAsync(DotMarcDbContext context, AuditActor actor, int domainId, IReadOnlyList<int> tagIds, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.Include(d => d.Tags).SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        var tags = await context.Tags.Where(t => tagIds.Contains(t.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Set("Tags", domain.Tags.Select(tag => tag.Name), tags.Select(tag => tag.Name));
        if (!changes.Any)
        {
            return;
        }

        domain.Tags = tags;
        AuditLog.Record(context, actor, AuditActions.DomainTagsChanged, AuditTarget.For(domain), $"Changed the tags for {domain.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
