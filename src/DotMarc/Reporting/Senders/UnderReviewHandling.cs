using DotMarc.Data;

namespace DotMarc.Reporting;

public enum UnderReviewMode
{
    /// <summary>Under review stops the new sender alert, as Authorised and Unauthorised do. Today's behaviour.</summary>
    CountsAsDecided,

    /// <summary>Under review changes nothing: the sender alerts as if nobody had decided.</summary>
    CountsAsUnreviewed,

    /// <summary>Under review stops the alert for a while, then the sender alerts again once it has stayed Under review that long.</summary>
    AlertAgainAfterDays
}

/// <summary>What Under review means for the new sender alert, as one rule sets it.</summary>
public sealed record UnderReviewHandling(UnderReviewMode Mode, int Days = 0)
{
    public static UnderReviewHandling Default { get; } = new(UnderReviewMode.CountsAsDecided);

    /// <summary>Whether a sender with this decision belongs in the new sender alert. <paramref name="isNew"/> is whether it first
    /// appeared within the rule's "new" window. With no decision, a sender alerts while it is new. Authorised and Unauthorised never
    /// alert. Under review follows the mode; alerting again after some days does not need the sender to be new, because by then it is
    /// not: the point is that the review has dragged on. The days run from when the decision that applies was made.</summary>
    public bool NeedsAlert(ResolvedSenderDecision decision, bool isNew, DateTimeOffset nowUtc)
    {
        if (decision.Status is null)
        {
            return isNew;
        }

        if (decision.Status != SenderDecisionStatus.UnderReview)
        {
            return false;
        }

        return Mode switch
        {
            UnderReviewMode.CountsAsUnreviewed => isNew,
            UnderReviewMode.AlertAgainAfterDays => decision.Decision is { } applied && applied.DecidedUtc <= nowUtc - TimeSpan.FromDays(Days),
            _ => false
        };
    }
}
