using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Baselines;

/// <summary>Reads the DMARC target that applies to a domain for the DNS push, so the change builder doesn't open a context.</summary>
public interface IDmarcTargetReader
{
    Task<EffectiveDmarcTarget?> GetAsync(int domainId, CancellationToken cancellationToken);
}

public sealed class DmarcTargetReader(IDbContextFactory<DotMarcDbContext> dbFactory) : IDmarcTargetReader
{
    public async Task<EffectiveDmarcTarget?> GetAsync(int domainId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var domain = await db.Domains.Include(candidate => candidate.Groups).FirstOrDefaultAsync(candidate => candidate.Id == domainId, cancellationToken).ConfigureAwait(false);
        if (domain is null)
        {
            return null;
        }

        var settings = await DnsRecordSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
        return DmarcTargetResolver.Resolve(domain, settings);
    }
}
