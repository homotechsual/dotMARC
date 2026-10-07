namespace DotMarc.Portal;

/// <summary>An uploaded logo, validated by <see cref="BrandingImages"/>. Each upload gets a new id, so an image never
/// changes once stored and can be cached for good.</summary>
public sealed class BrandingImage
{
    public Guid Id { get; set; }
    public string ContentType { get; set; } = "";
    public byte[] Bytes { get; set; } = [];
    public string Sha256 { get; set; } = "";
    public DateTimeOffset UploadedUtc { get; set; }
}
