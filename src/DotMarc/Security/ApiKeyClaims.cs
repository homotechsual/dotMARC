namespace DotMarc.Security;

/// <summary>Claims that mark a principal as an API key rather than a person. The key's name is the principal's name
/// claim.</summary>
public static class ApiKeyClaims
{
    public const string IdClaimType = "dotmarc:api-key-id";
    public const string CreatedByClaimType = "dotmarc:api-key-created-by";
}
