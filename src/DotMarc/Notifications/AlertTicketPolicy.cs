using DotMarc.Data;

namespace DotMarc.Notifications;

/// <summary>Decides whether an alert creates a HaloPSA ticket. Pure, so every case is a plain unit test.
/// Order: the deciding group's rule, then the global rule, then the alert type's registry default.</summary>
public static class AlertTicketPolicy
{
    /// <param name="domain">Must have its <c>Groups</c> loaded.</param>
    /// <param name="rules">The global rules and the deciding group's rules for this alert type. Rules for other
    /// groups or other alert types are ignored, so passing more than needed is harmless.</param>
    public static bool ShouldCreateTicket(string alertType, Domain domain, IReadOnlyCollection<AlertTicketRule> rules)
    {
        var decidingGroup = HaloClientResolver.ResolveGroup(domain);
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
