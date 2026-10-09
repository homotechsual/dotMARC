using DotMarc.Psa;
using DotMarc.Audit;
using DotMarc.Portal;
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

    /// <param name="haloClientId">Links the new group to this Halo client straight away, as when a group is created
    /// from a Halo client on Manage groups.</param>
    public static async Task<AddGroupResult> AddGroupAsync(DotMarcDbContext context, AuditActor actor, string rawName, CancellationToken cancellationToken = default, (PsaKind Psa, PsaCompany Company)? linkTo = null)
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

        var group = new Group { Name = name };
        var changes = new AuditChanges();
        if (linkTo is { } link)
        {
            group.PsaCompanyLinks.Add(new PsaCompanyLink { Psa = link.Psa, CompanyId = link.Company.Id, CompanyName = link.Company.Name });
            changes.Field(link.Psa.CompanyLabel(), (string?)null, link.Company.Name);
        }

        context.Groups.Add(group);

        try
        {
            await AuditLog.SaveAndRecordAsync(context,
                () => AuditLog.Record(context, actor, AuditActions.GroupAdded, AuditTarget.For(group), $"Added group {group.Name}",
                    changes),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // The unique index on Group.Name caught a same-cased race. A concurrent
            // different-cased duplicate (e.g. "Client A" vs "client a") is not caught by the
            // plain index - an accepted gap given group creation is a low-frequency manual
            // action, not the high-concurrency path Domain auto-discovery is.
            return AddGroupResult.AlreadyExists;
        }

        return AddGroupResult.Added;
    }

    public static async Task<AddGroupResult> RenameGroupAsync(DotMarcDbContext context, AuditActor actor, int groupId, string rawName, CancellationToken cancellationToken = default)
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
        var changes = new AuditChanges().Field("Name", group.Name, name);
        if (!changes.Any)
        {
            return AddGroupResult.Added;
        }

        AuditLog.Record(context, actor, AuditActions.GroupRenamed, AuditTarget.For(group), $"Renamed group {group.Name} to {name}", changes);
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

    public enum RemoveGroupResult { Removed, InUseByApiKey, LastGroupOfClientPortal }

    /// <summary>Permanently deletes a Group row. DotMarcDbContext.cs's implicit many-to-many
    /// skip navigation between Domain and Group means EF removes the join rows via the join
    /// table's own cascade-delete foreign key - no Domain or Report data is touched. Refused while
    /// an unrevoked API key is limited to the group: a key left with no groups would see every
    /// group, and a key can't be edited, so it has to be revoked first.</summary>
    public static async Task<RemoveGroupResult> RemoveGroupAsync(DotMarcDbContext context, AuditActor actor, int groupId, CancellationToken cancellationToken = default)
    {
        var group = await context.Groups.SingleAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        var limitsAnApiKey = await context.ApiKeys
            .AnyAsync(key => key.RevokedUtc == null && key.ScopedGroups.Any(scopedGroup => scopedGroup.Id == groupId), cancellationToken)
            .ConfigureAwait(false);
        if (limitsAnApiKey)
        {
            return RemoveGroupResult.InUseByApiKey;
        }

        // A client portal grant left with no Groups would see every domain, so its last Group can't be removed.
        var lastGroupOfAPortalGrant = await context.UserAccesses
            .AnyAsync(access => access.IsClientPortal && access.ScopedGroups.Count == 1 && access.ScopedGroups.Any(scopedGroup => scopedGroup.Id == groupId), cancellationToken)
            .ConfigureAwait(false);
        if (lastGroupOfAPortalGrant)
        {
            return RemoveGroupResult.LastGroupOfClientPortal;
        }

        // Its branding row goes with it by cascade; its logos are released for cleanup below.
        var branding = await context.GroupBrandings.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.GroupId == groupId, cancellationToken).ConfigureAwait(false);
        AuditLog.Record(context, actor, AuditActions.GroupRemoved, AuditTarget.For(group), $"Removed group {group.Name}");
        context.Groups.Remove(group);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (branding is not null)
        {
            await BrandingImageCleanup.DeleteUnreferencedAsync(context, new[] { branding.LogoImageId, branding.DarkLogoImageId }.OfType<Guid>(), cancellationToken).ConfigureAwait(false);
        }

        return RemoveGroupResult.Removed;
    }

    /// <summary>Replaces a domain's full set of group memberships with exactly the given group
    /// IDs - the multi-select on Manage Domains always submits the complete desired set, not an
    /// incremental add/remove.</summary>
    public static async Task SetDomainGroupsAsync(DotMarcDbContext context, AuditActor actor, int domainId, IReadOnlyList<int> groupIds, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.Include(d => d.Groups).SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        var groups = await context.Groups.Where(g => groupIds.Contains(g.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Set("Groups", domain.Groups.Select(group => group.Name), groups.Select(group => group.Name));
        if (!changes.Any)
        {
            return;
        }

        domain.Groups = groups;
        AuditLog.Record(context, actor, AuditActions.DomainGroupsChanged, AuditTarget.For(domain), $"Changed the groups for {domain.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Links (or, with null, unlinks) a Group to a company in one PSA, from that PSA's column on Manage groups.</summary>
    public static async Task SetPsaCompanyAsync(DotMarcDbContext context, AuditActor actor, int groupId, PsaKind psa, PsaCompany? company, CancellationToken cancellationToken = default)
    {
        var group = await context.Groups.Include(candidate => candidate.PsaCompanyLinks).SingleAsync(candidate => candidate.Id == groupId, cancellationToken).ConfigureAwait(false);
        var existing = group.PsaCompanyLinks.FirstOrDefault(link => link.Psa == psa);
        if (existing?.CompanyId == company?.Id)
        {
            return;
        }

        var changes = new AuditChanges().Field(psa.CompanyLabel(), existing?.CompanyName, company?.Name);
        if (PsaCompanyLinks.Apply(group.PsaCompanyLinks, existing, psa, company) is { } removed)
        {
            context.PsaCompanyLinks.Remove(removed);
        }

        AuditLog.Record(context, actor, AuditActions.GroupPsaCompanyChanged, AuditTarget.For(group), $"Changed the {psa.CompanyLabel()} for group {group.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public static Task<GroupBranding?> GetBrandingAsync(DotMarcDbContext context, int groupId, CancellationToken cancellationToken = default) =>
        context.GroupBrandings.AsNoTracking().SingleOrDefaultAsync(branding => branding.GroupId == groupId, cancellationToken);

    /// <summary>Sets a Group's own portal branding. Empty fields fall back to the MSP brand; clearing every field removes
    /// the Group's branding altogether. Logos the change stops using are deleted.</summary>
    public static async Task SetBrandingAsync(DotMarcDbContext context, AuditActor actor, int groupId, GroupBrandingInput input, CancellationToken cancellationToken = default)
    {
        var displayName = Blank(input.DisplayName);
        var primaryColour = Blank(input.PrimaryColour)?.ToUpperInvariant();
        var secondaryColour = Blank(input.SecondaryColour)?.ToUpperInvariant();
        if (displayName is { Length: > 100 }) throw new ArgumentException("Display name can be at most 100 characters.", nameof(input));
        if (primaryColour is not null && !BrandColours.IsValid(primaryColour)) throw new ArgumentException("Primary colour must be a hex colour such as #1A73E8.", nameof(input));
        if (secondaryColour is not null && !BrandColours.IsValid(secondaryColour)) throw new ArgumentException("Secondary colour must be a hex colour such as #1A73E8.", nameof(input));

        await BrandingImageCleanup.EnsureStoredAsync(context, [input.LogoImageId, input.DarkLogoImageId], cancellationToken).ConfigureAwait(false);

        var group = await context.Groups.SingleAsync(candidate => candidate.Id == groupId, cancellationToken).ConfigureAwait(false);
        var existing = await context.GroupBrandings.SingleOrDefaultAsync(branding => branding.GroupId == groupId, cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges()
            .Field("Display name", existing?.DisplayName, displayName)
            .Field("Logo", LogoState(existing?.LogoImageId, existing?.LogoImageId), LogoState(existing?.LogoImageId, input.LogoImageId))
            .Field("Dark logo", LogoState(existing?.DarkLogoImageId, existing?.DarkLogoImageId), LogoState(existing?.DarkLogoImageId, input.DarkLogoImageId))
            .Field("Primary colour", existing?.PrimaryColour, primaryColour)
            .Field("Secondary colour", existing?.SecondaryColour, secondaryColour);
        if (!changes.Any)
        {
            return;
        }

        var released = new[] { existing?.LogoImageId, existing?.DarkLogoImageId }.OfType<Guid>().ToList();
        var cleared = displayName is null && input.LogoImageId is null && input.DarkLogoImageId is null && primaryColour is null && secondaryColour is null;
        if (cleared)
        {
            if (existing is not null)
            {
                context.GroupBrandings.Remove(existing);
            }
        }
        else
        {
            if (existing is null)
            {
                existing = new GroupBranding { GroupId = groupId };
                context.GroupBrandings.Add(existing);
            }

            existing.DisplayName = displayName;
            existing.LogoImageId = input.LogoImageId;
            existing.DarkLogoImageId = input.DarkLogoImageId;
            existing.PrimaryColour = primaryColour;
            existing.SecondaryColour = secondaryColour;
        }

        AuditLog.Record(context, actor, AuditActions.GroupBrandingChanged, AuditTarget.For(group), $"Changed the portal branding for group {group.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BrandingImageCleanup.DeleteUnreferencedAsync(context, released, cancellationToken).ConfigureAwait(false);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // "None", "Set", or "Replaced" when a different image takes the place of the saved one.
    private static string LogoState(Guid? savedId, Guid? id) =>
        id is null ? "None" : savedId is null || savedId == id ? "Set" : "Replaced";
}
