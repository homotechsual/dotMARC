using System.Security.Cryptography;
using DotMarc.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public sealed class DatabaseSecretStore : ISecretStore
{
    private readonly IDbContextFactory<DotMarcDbContext> _dbFactory;
    private readonly IDataProtector _protector;

    public DatabaseSecretStore(IDbContextFactory<DotMarcDbContext> dbFactory, IDataProtectionProvider dataProtectionProvider)
    {
        _dbFactory = dbFactory;
        _protector = dataProtectionProvider.CreateProtector("DotMarc.Notifications.EncryptedSecret.v1");
    }

    public async Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var protectedValue = _protector.Protect(value);
        var existing = await context.EncryptedSecrets.SingleOrDefaultAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            context.EncryptedSecrets.Add(new EncryptedSecret { Key = key, ProtectedValue = protectedValue });
        }
        else
        {
            existing.ProtectedValue = protectedValue;
        }
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await context.EncryptedSecrets.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(existing.ProtectedValue);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
