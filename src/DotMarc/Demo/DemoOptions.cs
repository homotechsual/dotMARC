using System.ComponentModel.DataAnnotations;

namespace DotMarc.Demo;

/// <summary>Gates every demo-mode addition in this app. See
/// docs/superpowers/specs/2026-08-28-demo-instance-design.md. When Enabled is false (the
/// default), nothing in the DotMarc.Demo namespace runs — real Entra/Graph auth and ingestion
/// behave exactly as before this feature existed.</summary>
public sealed class DemoOptions
{
    public const string SectionName = "Demo";

    public bool Enabled { get; set; }

    /// <summary>UTC hour DemoDataResetService resets the dataset each day. Range-validated
    /// because DemoDataResetService.GetDelayUntilNextReset constructs a DateTimeOffset directly
    /// from this value outside any try/catch in BackgroundService.ExecuteAsync's loop — an
    /// out-of-range value would otherwise throw ArgumentOutOfRangeException there and crash the
    /// whole host under .NET's default BackgroundServiceExceptionBehavior.StopHost.</summary>
    [Range(0, 23)]
    public int ResetHourUtc { get; set; } = 4;
}
