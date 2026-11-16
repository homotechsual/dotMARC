using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.DnsPush;

/// <summary>Reads the admin's DNS record settings for a change builder, so the builders don't each open a context.</summary>
public interface IDnsRecordSettingsReader
{
    Task<DnsRecordSettings> GetAsync(CancellationToken cancellationToken);
}

public sealed class DnsRecordSettingsReader(IDbContextFactory<DotMarcDbContext> dbFactory) : IDnsRecordSettingsReader
{
    public async Task<DnsRecordSettings> GetAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await DnsRecordSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
    }
}
