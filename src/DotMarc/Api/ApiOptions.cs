namespace DotMarc.Api;

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>Requests each key may make per minute.</summary>
    public int RequestsPerMinute { get; set; } = 120;
}
