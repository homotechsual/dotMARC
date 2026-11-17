namespace DotMarc.Data;

/// <summary>One person's saved Dashboard defaults, keyed by who they are (their Entra object ID, or their email when
/// there isn't one). The settings are kept as JSON so adding a filter later doesn't need a migration.</summary>
public sealed class UserDashboardPreference
{
    public int Id { get; set; }
    public required string UserKey { get; set; }
    public required string SettingsJson { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }

    public const int MaxUserKeyLength = 200;
}
