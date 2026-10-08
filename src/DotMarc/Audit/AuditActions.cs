namespace DotMarc.Audit;

/// <summary>Every action the audit log records. These codes are stored, so they must never change once shipped.</summary>
public static class AuditActions
{
    public const string DomainAdded = "domain.added";
    public const string DomainRemoved = "domain.removed";
    public const string DomainMonitoringChanged = "domain.monitoring_changed";
    public const string DomainHaloClientChanged = "domain.halo_client_changed";
    public const string DomainPsaCompanyChanged = "domain.psa_company_changed";
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
    public const string GroupPsaCompanyChanged = "group.psa_company_changed";
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
    public const string AccessClientPortalChanged = "access.client_portal_changed";
    public const string AlertResolvedByTicket = "alert.resolved_by_ticket";
    public const string NotificationSettingsSaved = "settings.notifications.saved";
    public const string HaloSettingsSaved = "settings.halo.saved";
    public const string ConnectWiseSettingsSaved = "settings.connectwise.saved";
    public const string AutotaskSettingsSaved = "settings.autotask.saved";
    public const string BrandingSettingsSaved = "settings.branding.saved";
    public const string EmailSettingsSaved = "settings.email.saved";
    public const string ReportSettingsSaved = "settings.reports.saved";
    public const string GroupReportScheduleChanged = "group.report_schedule_changed";
    public const string ClientReportSent = "group.report_sent";
    public const string GroupBrandingChanged = "group.branding_changed";
    public const string CloudflareDnsSettingsSaved = "settings.cloudflare_dns.saved";
    public const string AzureDnsSettingsSaved = "settings.azure_dns.saved";
    public const string GoogleCloudDnsSettingsSaved = "settings.google_cloud_dns.saved";
    public const string DnsRecordSettingsSaved = "settings.dns_records.saved";
    public const string AuditSettingsSaved = "settings.audit.saved";
    public const string DnsPushed = "dns.pushed";
    public const string DnsPushFailed = "dns.push_failed";
    public const string HaloIntegrationTested = "halo.integration_tested";
    public const string ConnectWiseIntegrationTested = "connectwise.integration_tested";
    public const string AutotaskIntegrationTested = "autotask.integration_tested";
    public const string AutotaskZoneCleared = "autotask.zone_cleared";
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
        (DomainPsaCompanyChanged, "Domain PSA company changed"),
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
        (GroupPsaCompanyChanged, "Group PSA company changed"),
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
        (AccessClientPortalChanged, "Client portal access changed"),
        (AlertResolvedByTicket, "Alert resolved by a closed PSA ticket"),
        (NotificationSettingsSaved, "Notification settings saved"),
        (HaloSettingsSaved, "HaloPSA settings saved"),
        (ConnectWiseSettingsSaved, "ConnectWise settings saved"),
        (AutotaskSettingsSaved, "Autotask settings saved"),
        (BrandingSettingsSaved, "Branding saved"),
        (EmailSettingsSaved, "Email settings saved"),
        (ReportSettingsSaved, "Report settings saved"),
        (GroupReportScheduleChanged, "Group report schedule changed"),
        (ClientReportSent, "Client report sent"),
        (GroupBrandingChanged, "Group branding changed"),
        (CloudflareDnsSettingsSaved, "Cloudflare DNS settings saved"),
        (AzureDnsSettingsSaved, "Azure DNS settings saved"),
        (GoogleCloudDnsSettingsSaved, "Google Cloud DNS settings saved"),
        (DnsRecordSettingsSaved, "DNS record settings saved"),
        (AuditSettingsSaved, "Audit retention changed"),
        (DnsPushed, "DNS records pushed"),
        (DnsPushFailed, "DNS push failed partway"),
        (HaloIntegrationTested, "HaloPSA integration tested"),
        (ConnectWiseIntegrationTested, "ConnectWise integration tested"),
        (AutotaskIntegrationTested, "Autotask integration tested"),
        (AutotaskZoneCleared, "Autotask zone cleared"),
        (HaloSignInCleared, "HaloPSA sign-in cleared"),
        (AuditExported, "Audit log exported"),
        (SignInSucceeded, "Signed in"),
        (SignInRefused, "Sign-in refused"),
        (PageViewed, "Page viewed"),
    ];
}
