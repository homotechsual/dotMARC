namespace DotMarc.Api;

/// <summary>The sections the API's operations are grouped into in the OpenAPI document, and so in the website's API
/// reference. Without these, each operation is tagged with the C# class that maps it.</summary>
public static class ApiTags
{
    public const string Domains = "Domains";
    public const string Imports = "Imports";
    public const string GroupsAndTags = "Groups and tags";
    public const string Alerts = "Alerts";

    public static IReadOnlyList<(string Name, string Description)> All { get; } =
    [
        (Domains, "List domains with their DNS health and DMARC report summaries, add a domain, and set its groups, tags and monitoring."),
        (Imports, "Add and update many domains at once, as the Import domains page does."),
        (GroupsAndTags, "The groups and tags domains are organised by."),
        (Alerts, "List alerts and acknowledge DMARC policy and nameserver alerts."),
    ];
}
