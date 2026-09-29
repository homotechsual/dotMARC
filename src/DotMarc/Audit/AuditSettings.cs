namespace DotMarc.Audit;

/// <summary>The single row of audit retention settings, seeded by DotMarcDbContext. A null period keeps that kind
/// of entry forever.</summary>
public sealed class AuditSettings
{
    public const int MinimumRetentionDays = 1;
    public const int MaximumRetentionDays = 3650;

    public int Id { get; set; }
    public int? ChangeRetentionDays { get; set; } = 365;
    public int? SignInRetentionDays { get; set; } = 365;
    public int? PageViewRetentionDays { get; set; } = 90;
}
