namespace DotMarc.Api;

/// <summary>The sections the API's operations are grouped into in the OpenAPI document, and so in the website's API
/// reference. Without these, each operation is tagged with the C# class that maps it.</summary>
public static class ApiTags
{
    public const string Domains = "Domains";
    public const string Imports = "Imports";
    public const string GroupsAndTags = "Groups and tags";
    public const string Alerts = "Alerts";
    public const string AlertRules = "Alert rules";
    public const string AuditLog = "Audit log";
    public const string Reports = "Reports";
    public const string ClientReports = "Client reports";
    public const string ParseFailures = "Parse failures";

    public static IReadOnlyList<(string Name, string Description)> All { get; } =
    [
        (Domains, "List domains with their DNS health and DMARC report summaries, add a domain, and set its groups, tags and monitoring."),
        (Imports, "Add and update many domains at once, as the Import domains page does."),
        (GroupsAndTags, "The groups and tags domains are organised by."),
        (Alerts, "List alerts and acknowledge DMARC policy and nameserver alerts."),
        (AlertRules, "Read the alert rules that decide when each kind of alert is raised and where it goes, and the names of the destinations they send to."),
        (AuditLog, "Who did what in dotMARC: changes, sign-ins and, on request, pages opened."),
        (Reports, "Sending sources, DMARC and TLS reports with their original files, and the dashboard's headline figures."),
        (ClientReports, "Each group's report schedule and delivery history, report PDFs for any period, and sending a report now."),
        (ParseFailures, "Report emails dotMARC couldn't read, and why."),
    ];
}
