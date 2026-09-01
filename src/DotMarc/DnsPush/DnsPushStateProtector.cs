using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace DotMarc.DnsPush;

/// <summary>Encodes a DnsPushState into an opaque, tamper-proof string carried as the OAuth `state`
/// parameter across the redirect to the provider and back — avoids needing any server-side session
/// between /dns-push/{provider}/start and .../callback. Short-lived (5 minutes): a state value used
/// after that window is rejected, same reasoning as an OIDC nonce.</summary>
public sealed class DnsPushStateProtector
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly IDataProtector _protector;

    public DnsPushStateProtector(IDataProtectionProvider dataProtectionProvider) =>
        _protector = dataProtectionProvider.CreateProtector("DotMarc.DnsPush.State.v1");

    public string Protect(int domainId, string pushTarget, string codeVerifier, DateTimeOffset nowUtc)
    {
        var state = new DnsPushState(domainId, pushTarget, codeVerifier, nowUtc.Add(Lifetime));
        return _protector.Protect(JsonSerializer.Serialize(state));
    }

    /// <summary>Returns null if the value is malformed, was tampered with, or has expired.</summary>
    public DnsPushState? Unprotect(string protectedState, DateTimeOffset nowUtc)
    {
        string json;
        try
        {
            json = _protector.Unprotect(protectedState);
        }
        catch (CryptographicException)
        {
            return null;
        }

        var state = JsonSerializer.Deserialize<DnsPushState>(json);
        return state is not null && state.ExpiresAtUtc > nowUtc ? state : null;
    }
}
