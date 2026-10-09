using DotMarc.Data;
using DotMarc.Psa;

namespace DotMarc.Notifications;

/// <summary>Decides whether an alert creates a ticket in one PSA. Pure, so every case is a plain unit test.
/// Order: the deciding group's rule, then the global rule, then the alert type's registry default.</summary>
public static class AlertTicketPolicy
{
    /// <param name="domain">Must have its links, its <c>Groups</c> and their links loaded (see <see cref="PsaCompanyResolver.IncludeLinks"/>).</param>
    /// <param name="rules">The global rules and the deciding group's rules for this alert type. Rules for other
    /// groups or other alert types are ignored, so passing more than needed is harmless.</param>
    public static bool ShouldCreateTicket(string alertType, Domain domain, PsaKind psa, IReadOnlyCollection<AlertTicketRule> rules)
    {
        var decidingGroup = PsaCompanyResolver.ResolveGroup(domain, psa);
        if (decidingGroup is not null)
        {
            var groupRule = rules.FirstOrDefault(rule => rule.AlertType == alertType && rule.GroupId == decidingGroup.Id);
            if (groupRule is not null)
            {
                return groupRule.CreateTicket;
            }
        }

        var globalRule = rules.FirstOrDefault(rule => rule.AlertType == alertType && rule.GroupId is null);
        if (globalRule is not null)
        {
            return globalRule.CreateTicket;
        }

        return AlertTypes.Find(alertType)?.CreatesTicketByDefault ?? true;
    }
}
