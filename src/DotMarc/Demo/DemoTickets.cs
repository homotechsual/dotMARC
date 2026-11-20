using DotMarc.Notifications;
using DotMarc.Psa;

namespace DotMarc.Demo;

/// <summary>The tickets the demo data starts with: one open ticket in each PSA, each on a different open alert, plus a closed one on a
/// resolved alert, so the Alerts page shows every PSA's chip and both states. The demo PSAs are pretend and have no web address, so
/// the chips are not links there. Ids are below the demo PSA's own counter (which starts at 5001) so a ticket it raises later
/// never collides, and are written in each PSA's own style.</summary>
public static class DemoTickets
{
    public sealed record Sample(string DomainName, string AlertType, PsaKind Psa, string TicketId, bool IsOpen);

    public static IReadOnlyList<Sample> Samples { get; } =
    [
        new("fleet.cobalt-freight.example", "MissedReport", PsaKind.HaloPsa, "4107", true),
        new("cobalt-freight.example", "TlsrptFailure", PsaKind.NinjaOne, "3187", true),
        new("brightline-legal.example", AlertTypes.SpfRecordBroken, PsaKind.ConnectWise, "2214", true),
        new("aurora-retail.example", AlertTypes.DmarcPolicyWeakened, PsaKind.Autotask, "T20260612.0007", true),
        new("cobalt-freight.example", AlertTypes.UnauthorisedSenderDelivered, PsaKind.SuperOps, "INC-1042", true),
        new("shop.aurora-retail.example", "TlsrptFailure", PsaKind.HaloPsa, "4082", false),
    ];

    /// <summary>True for a ticket the demo data starts with that is still open. The demo PSA reports these as open for ever,
    /// because it knows only the tickets it raised itself and would otherwise report the rest as gone.</summary>
    public static bool IsSeededOpen(PsaKind psa, string ticketId) =>
        Samples.Any(sample => sample.IsOpen && sample.Psa == psa && sample.TicketId == ticketId);
}
