using System.Globalization;
using DotMarc.Data;

namespace DotMarc.Reporting;

/// <summary>The words the sender table uses for a decision: its label, where it came from, and the fix text, which depends on
/// what was decided and whether the mail is being blocked.</summary>
public static class SenderDecisionPresentation
{
    public static string StatusLabel(SenderDecisionStatus? status) => status?.Label() ?? "Unreviewed";

    /// <summary>"set on this domain", "from the Aurora Retail group" or "from the baseline", or empty when nothing was decided.</summary>
    public static string Provenance(ResolvedSenderDecision decision, IReadOnlyDictionary<int, string> groupNames) => decision.Level switch
    {
        SenderDecisionLevel.Domain => "set on this domain",
        SenderDecisionLevel.Group => $"from the {groupNames.GetValueOrDefault(decision.Decision?.GroupId ?? 0, "group")} group",
        SenderDecisionLevel.Baseline => "from the baseline",
        _ => string.Empty,
    };

    /// <summary>What the Fix column says for this row. Unreviewed, under review and any decision without a stored row keep
    /// <see cref="SenderServiceRow.Fix"/>. An authorised sender that is failing gets a louder fix, never a quieter one.</summary>
    public static string? FixFor(SenderServiceRow row, ResolvedSenderDecision decision, DmarcPolicyLevel policy)
    {
        if (decision.Decision is null)
        {
            return row.Fix;
        }

        switch (decision.Status)
        {
            case SenderDecisionStatus.Authorised:
                return row.Failing == 0
                    ? null
                    : $"You authorised {row.Name}, but {Count(row.Failing)} messages fail DMARC. {row.Fix}";

            case SenderDecisionStatus.Unauthorised:
                var madeBy = $"Marked unauthorised by {decision.Decision.DecidedByName} on {decision.Decision.DecidedUtc.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}";
                if (row.Failing == 0)
                {
                    return $"{madeBy}, but {Count(row.Passing)} messages from it pass DMARC. It authenticates as your domain, so check whether it should be authorised or remove its access.";
                }

                var delivered = row.Failing - row.ActedOn;
                return delivered <= 0
                    ? $"{madeBy}. Your DMARC policy is blocking it."
                    : $"{madeBy}. {Count(delivered)} messages were still delivered. {PolicyLine(policy)}";

            default:
                return row.Fix;
        }
    }

    private static string PolicyLine(DmarcPolicyLevel policy) => policy switch
    {
        DmarcPolicyLevel.None => "Your policy is none, so this mail is still delivered. Move to quarantine or reject to block it.",
        DmarcPolicyLevel.Quarantine => "Your policy is quarantine, so receivers should send it to spam.",
        _ => "Your policy is reject, so receivers should be rejecting it.",
    };

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
