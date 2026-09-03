using System.Security.Cryptography;
using DotMarc.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public sealed class DatabaseHaloSecretStore : IHaloSecretStore
{
    private readonly IDbContextFactory<DotMarcDbContext> _dbFactory;
    private readonly IDataProtector _protector;

    public DatabaseHaloSecretStore(IDbContextFactory<DotMarcDbContext> dbFactory, IDataProtectionProvider dataProtectionProvider)
    {
        _dbFactory = dbFactory;
        _protector = dataProtectionProvider.CreateProtector("DotMarc.Notifications.HaloPsaClientSecret.v1");
    }

    public async Task SetClientSecretAsync(string clientSecret, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await context.HaloPsaSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        settings.ProtectedClientSecret = _protector.Protect(clientSecret);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetClientSecretAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await context.HaloPsaSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(settings.ProtectedClientSecret))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(settings.ProtectedClientSecret);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
