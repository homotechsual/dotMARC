namespace DotMarc.Psa;

/// <summary>Which company in one PSA a Group's or Domain's tickets go to. Exactly one of <see cref="GroupId"/> and
/// <see cref="DomainId"/> is set (a check constraint enforces it). A Domain's link overrides its Groups' for that PSA.</summary>
public sealed class PsaCompanyLink
{
    public int Id { get; set; }
    public PsaKind Psa { get; set; }
    public int? GroupId { get; set; }
    public int? DomainId { get; set; }
    public required string CompanyId { get; set; }

    /// <summary>For display only, refreshed whenever a page loads the PSA's company list. Nothing sent to the PSA uses it.</summary>
    public required string CompanyName { get; set; }
}
