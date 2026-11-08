namespace DotMarc.Psa;

/// <summary>A PSA dotMARC can raise tickets in. Stored by name, so the order here can change freely. The order is the
/// order PSAs are listed in everywhere.</summary>
public enum PsaKind { HaloPsa, NinjaOne, ConnectWise, Autotask, SuperOps }

public static class PsaKindNames
{
    public static string DisplayName(this PsaKind psa) => psa switch
    {
        PsaKind.HaloPsa => "HaloPSA",
        PsaKind.NinjaOne => "NinjaOne",
        PsaKind.ConnectWise => "ConnectWise",
        PsaKind.Autotask => "Autotask",
        PsaKind.SuperOps => "SuperOps",
        _ => psa.ToString()
    };

    /// <summary>What the PSA calls the customer a ticket is raised against, as a column or field label.</summary>
    public static string CompanyLabel(this PsaKind psa) => psa switch
    {
        PsaKind.HaloPsa => "Halo client",
        PsaKind.NinjaOne => "NinjaOne organisation",
        PsaKind.ConnectWise => "ConnectWise company",
        PsaKind.Autotask => "Autotask company",
        PsaKind.SuperOps => "SuperOps client",
        _ => $"{psa} company"
    };
}
