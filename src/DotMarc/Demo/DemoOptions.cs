namespace DotMarc.Demo;

/// <summary>Gates every demo-mode addition in this app. See
/// docs/superpowers/specs/2026-08-28-demo-instance-design.md. When Enabled is false (the
/// default), nothing in the DotMarc.Demo namespace runs — real Entra/Graph auth and ingestion
/// behave exactly as before this feature existed.</summary>
public sealed class DemoOptions
{
    public const string SectionName = "Demo";

    public bool Enabled { get; set; }

    /// <summary>UTC hour DemoDataResetService resets the dataset each day.</summary>
    public int ResetHourUtc { get; set; } = 4;
}
