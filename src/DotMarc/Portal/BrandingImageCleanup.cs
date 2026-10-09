using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Portal;

/// <summary>Removes logos nothing uses any more (replaced, cleared, or uploaded but never saved).</summary>
public static class BrandingImageCleanup
{
    /// <summary>Throws when a logo id being saved has no image, such as an upload left unsaved for over an hour and
    /// cleaned up in the meantime, rather than saving a logo that would show as a broken image.</summary>
    public static async Task EnsureStoredAsync(DotMarcDbContext context, IEnumerable<Guid?> imageIds, CancellationToken cancellationToken)
    {
        var wanted = imageIds.OfType<Guid>().Distinct().ToList();
        if (wanted.Count == 0)
        {
            return;
        }

        var stored = await context.BrandingImages.CountAsync(image => wanted.Contains(image.Id), cancellationToken).ConfigureAwait(false);
        if (stored != wanted.Count)
        {
            throw new ArgumentException(BrandingImages.ExpiredUpload, nameof(imageIds));
        }
    }

    /// <summary>Deletes every image nothing references that was either released by the save just made, or uploaded more
    /// than an hour ago. A newer unreferenced upload may belong to a form still being filled in, so it's left for later.</summary>
    public static async Task DeleteUnreferencedAsync(DotMarcDbContext context, IEnumerable<Guid> justReleased, CancellationToken cancellationToken)
    {
        var settings = await context.BrandingSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var inUse = new HashSet<Guid>(new[] { settings.LogoImageId, settings.DarkLogoImageId }.OfType<Guid>());
        foreach (var groupBranding in await context.GroupBrandings.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            inUse.UnionWith(new[] { groupBranding.LogoImageId, groupBranding.DarkLogoImageId }.OfType<Guid>());
        }

        var released = justReleased.Where(id => !inUse.Contains(id)).ToList();
        var recentCutoff = DateTimeOffset.UtcNow.AddHours(-1);
        await context.BrandingImages
            .Where(image => !inUse.Contains(image.Id) && (released.Contains(image.Id) || image.UploadedUtc < recentCutoff))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
