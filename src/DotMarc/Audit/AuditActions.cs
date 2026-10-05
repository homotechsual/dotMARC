namespace DotMarc.Audit;

/// <summary>Every action the audit log records. These codes are stored, so they must never change once shipped.</summary>
public static class AuditActions
{
    public const string DomainAdded = "domain.added";
    public const string DomainRemoved = "domain.removed";
    public const string DomainMonitoringChanged = "domain.monitoring_changed";
    public const string DomainHaloClientChanged = "domain.halo_client_changed";
    public const string DomainMtaStsChanged = "domain.mta_sts_changed";
    public const string DomainDkimSelectorsChanged = "domain.dkim_selectors_changed";
    public const string DomainDkimRecordsChanged = "domain.dkim_records_changed";
    public const string DomainsReordered = "domains.reordered";
    public const string DomainsImported = "domains.imported";
    public const string DomainGroupsChanged = "domain.groups_changed";
    public const string DomainTagsChanged = "domain.tags_changed";
    public const string GroupAdded = "group.added";
    public const string GroupRenamed = "group.renamed";
    public const string GroupRemoved = "group.removed";
    public const string GroupHaloClientChanged = "group.halo_client_changed";
    public const string TagAdded = "tag.added";
    public const string TagUpdated = "tag.updated";
    public const string TagRemoved = "tag.removed";
    public const string RoleAdded = "role.added";
    public const string RoleUpdated = "role.updated";
    public const string RoleRemoved = "role.removed";
    public const string AccessGranted = "access.granted";
    public const string AccessUpdated = "access.updated";
    public const string AccessRevoked = "access.revoked";
    public const string ApiKeyCreated = "api_key.created";
    public const string ApiKeyRevoked = "api_key.revoked";
    public const string TicketRuleGlobalChanged = "ticket_rule.global_changed";
    public const string TicketRuleGroupChanged = "ticket_rule.group_changed";
    public const string AlertAcknowledged = "alert.acknowledged";
    public const string NotificationSettingsSaved = "settings.notifications.saved";
    public const string HaloSettingsSaved = "settings.halo.saved";
    public const string CloudflareDnsSettingsSaved = "settings.cloudflare_dns.saved";
    public const string AzureDnsSettingsSaved = "settings.azure_dns.saved";
    public const string GoogleCloudDnsSettingsSaved = "settings.google_cloud_dns.saved";
    public const string DnsRecordSettingsSaved = "settings.dns_records.saved";
    public const string AuditSettingsSaved = "settings.audit.saved";
    public const string DnsPushed = "dns.pushed";
    public const string DnsPushFailed = "dns.push_failed";
    public const string HaloIntegrationTested = "halo.integration_tested";
    public const string HaloSignInCleared = "halo.sign_in_cleared";
    public const string AuditExported = "audit.exported";
    public const string SignInSucceeded = "signin.succeeded";
    public const string SignInRefused = "signin.refused";
    public const string PageViewed = "page.viewed";

    /// <summary>Every action with a short label, for the Action filter on the Audit log page.</summary>
    public static IReadOnlyList<(string Action, string Label)> All { get; } =
    [
        (DomainAdded, "Domain added"),
        (DomainRemoved, "Domain removed"),
        (DomainMonitoringChanged, "Domain monitoring changed"),
        (DomainHaloClientChanged, "Domain Halo client changed"),
        (DomainMtaStsChanged, "Domain MTA-STS changed"),
        (DomainDkimSelectorsChanged, "Domain DKIM selectors changed"),
        (DomainDkimRecordsChanged, "Domain DKIM records changed"),
        (DomainsReordered, "Domains reordered"),
        (DomainsImported, "Domains imported"),
        (DomainGroupsChanged, "Domain groups changed"),
        (DomainTagsChanged, "Domain tags changed"),
        (GroupAdded, "Group added"),
        (GroupRenamed, "Group renamed"),
        (GroupRemoved, "Group removed"),
        (GroupHaloClientChanged, "Group Halo client changed"),
        (TagAdded, "Tag added"),
        (TagUpdated, "Tag updated"),
        (TagRemoved, "Tag removed"),
        (RoleAdded, "Role added"),
        (RoleUpdated, "Role updated"),
        (RoleRemoved, "Role removed"),
        (AccessGranted, "Access granted"),
        (AccessUpdated, "Access updated"),
        (AccessRevoked, "Access revoked"),
        (ApiKeyCreated, "API key created"),
        (ApiKeyRevoked, "API key revoked"),
        (TicketRuleGlobalChanged, "Ticket rule changed"),
        (TicketRuleGroupChanged, "Group ticket rule changed"),
        (AlertAcknowledged, "Alert acknowledged"),
        (NotificationSettingsSaved, "Notification settings saved"),
        (HaloSettingsSaved, "HaloPSA settings saved"),
        (CloudflareDnsSettingsSaved, "Cloudflare DNS settings saved"),
        (AzureDnsSettingsSaved, "Azure DNS settings saved"),
        (GoogleCloudDnsSettingsSaved, "Google Cloud DNS settings saved"),
        (DnsRecordSettingsSaved, "DNS record settings saved"),
        (AuditSettingsSaved, "Audit retention changed"),
        (DnsPushed, "DNS records pushed"),
        (DnsPushFailed, "DNS push failed partway"),
        (HaloIntegrationTested, "HaloPSA integration tested"),
        (HaloSignInCleared, "HaloPSA sign-in cleared"),
        (AuditExported, "Audit log exported"),
        (SignInSucceeded, "Signed in"),
        (SignInRefused, "Sign-in refused"),
        (PageViewed, "Page viewed"),
    ];
}
