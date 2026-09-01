using System.Security.Cryptography;
using System.Text;

namespace DotMarc.DnsPush;

/// <summary>Generates a PKCE code_verifier/code_challenge pair (RFC 7636, S256 method) for the
/// OAuth authorization-code exchange — used even for these confidential/server-side clients as
/// defense in depth on the code exchange, per the design spec.</summary>
public static class PkceGenerator
{
    public static (string CodeVerifier, string CodeChallenge) Generate()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var codeVerifier = Base64UrlEncode(verifierBytes);

        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var codeChallenge = Base64UrlEncode(challengeBytes);

        return (codeVerifier, codeChallenge);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
