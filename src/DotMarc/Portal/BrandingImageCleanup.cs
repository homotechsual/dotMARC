using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Portal;

/// <summary>Removes logos nothing uses any more (replaced, cleared, or uploaded but never saved).</summary>
public static class BrandingImageCleanup
{
    /// <summary>Deletes every image nothing references that was either released by the save just made, or uploaded more
    /// than an hour ago. A newer unreferenced upload may belong to a form still being filled in, so it's left for later.</summary>
    public static async Task DeleteUnreferencedAsync(DotMarcDbContext context, IEnumerable<Guid> justReleased, CancellationToken cancellationToken)
    {
        var settings = await context.BrandingSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var inUse = new HashSet<Guid>(new[] { settings.LogoImageId, settings.DarkLogoImageId }.OfType<Guid>());

        var released = justReleased.Where(id => !inUse.Contains(id)).ToList();
        var recentCutoff = DateTimeOffset.UtcNow.AddHours(-1);
        await context.BrandingImages
            .Where(image => !inUse.Contains(image.Id) && (released.Contains(image.Id) || image.UploadedUtc < recentCutoff))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
