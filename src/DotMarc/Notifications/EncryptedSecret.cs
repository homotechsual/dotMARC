namespace DotMarc.Notifications;

/// <summary>One row per secret key, written and read only by DatabaseSecretStore. ProtectedValue
/// is Data Protection-encrypted ciphertext, never plaintext.</summary>
public sealed class EncryptedSecret
{
    public required string Key { get; set; }
    public required string ProtectedValue { get; set; }
}
