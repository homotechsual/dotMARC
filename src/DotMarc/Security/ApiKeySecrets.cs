using System.Security.Cryptography;
using System.Text;

namespace DotMarc.Security;

/// <summary>Makes and hashes API key secrets. A secret carries 256 random bits, so a plain SHA-256 is enough to store
/// it: there is nothing to brute-force that a salt or a slow hash would protect.</summary>
public static class ApiKeySecrets
{
    public const string Prefix = "dmk_";
    private const int DisplayPrefixLength = 12;

    public static string Generate() => Prefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static string DisplayPrefix(string secret) => secret[..Math.Min(DisplayPrefixLength, secret.Length)];

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
