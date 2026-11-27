using DotMarc.Data;
using DotMarc.Baselines;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.DnsPush;

/// <summary>Reads the admin's DNS record settings for a change builder, so the builders don't each open a context.</summary>
public interface IDnsRecordSettingsReader
{
    Task<DnsRecordSettings> GetAsync(CancellationToken cancellationToken);
    async Task<DmarcReportAddress> GetReportAddressAsync(int domainId, CancellationToken cancellationToken) =>
        new((await GetAsync(cancellationToken)).AdditionalRuaAddress, "System default");
}

public sealed class DnsRecordSettingsReader(IDbContextFactory<DotMarcDbContext> dbFactory) : IDnsRecordSettingsReader
{
    public async Task<DmarcReportAddress> GetReportAddressAsync(int domainId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await db.Domains.Include(domain => domain.Groups).SingleAsync(domain => domain.Id == domainId, cancellationToken);
        return DmarcReportAddress.Resolve(domain, await DnsRecordSettingsService.GetAsync(db, cancellationToken));
    }

    public async Task<DnsRecordSettings> GetAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await DnsRecordSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
    }
}
