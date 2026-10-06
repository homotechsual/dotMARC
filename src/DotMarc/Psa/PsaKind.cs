namespace DotMarc.Psa;

/// <summary>A PSA dotMARC can raise tickets in. Stored by name, so the order here can change freely.</summary>
public enum PsaKind { HaloPsa, ConnectWise, Autotask }

public static class PsaKindNames
{
    public static string DisplayName(this PsaKind psa) => psa switch
    {
        PsaKind.HaloPsa => "HaloPSA",
        PsaKind.ConnectWise => "ConnectWise",
        PsaKind.Autotask => "Autotask",
        _ => psa.ToString()
    };

    /// <summary>What the PSA calls the customer a ticket is raised against, as a column or field label.</summary>
    public static string CompanyLabel(this PsaKind psa) => psa switch
    {
        PsaKind.HaloPsa => "Halo client",
        PsaKind.ConnectWise => "ConnectWise company",
        PsaKind.Autotask => "Autotask company",
        _ => $"{psa} company"
    };
}
