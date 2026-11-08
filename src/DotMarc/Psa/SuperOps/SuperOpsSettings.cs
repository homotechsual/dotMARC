namespace DotMarc.Psa.SuperOps;

/// <summary>Singleton settings row for SuperOps, seeded by its migration like the other PSAs. The API token lives in
/// ISecretStore under <see cref="ApiTokenSecretKey"/>, never on this entity; <see cref="ApiTokenConfigured"/> is the
/// only signal that one is saved.
///
/// SuperOps takes names, not numeric ids, for a ticket's status, priority, request type and so on, so the choices are
/// saved as the names SuperOps sends and expects back.</summary>
public sealed class SuperOpsSettings
{
    public const string ApiTokenSecretKey = "SuperOps.ApiToken";

    public const string DefaultRequestType = "Incident";
    public const string DefaultNewStatus = "Open";
    public const string DefaultClosedStatus = "Resolved";

    public int Id { get; set; }
    public bool Enabled { get; set; }

    /// <summary>The data centre the account is in: <see cref="SuperOpsRegions.Us"/> or <see cref="SuperOpsRegions.Eu"/>.</summary>
    public string Region { get; set; } = SuperOpsRegions.Us;

    /// <summary>The account's subdomain, the part before .superops.ai in its web address. SuperOps wants it on every call.</summary>
    public string? Subdomain { get; set; }
    public bool ApiTokenConfigured { get; set; }

    public string? RequestType { get; set; } = DefaultRequestType;

    /// <summary>The status new tickets start in.</summary>
    public string? NewStatus { get; set; } = DefaultNewStatus;

    /// <summary>The status dotMARC moves a ticket to when its alert resolves. A ticket a tech moves to this status, or to
    /// Resolved or Closed, resolves the alert.</summary>
    public string? ClosedStatus { get; set; } = DefaultClosedStatus;

    // Optional: left empty, SuperOps applies its own default.
    public string? Priority { get; set; }
    public string? Urgency { get; set; }
    public string? Category { get; set; }
}

public static class SuperOpsRegions
{
    public const string Us = "us";
    public const string Eu = "eu";

    public static IReadOnlyList<string> All { get; } = [Us, Eu];

    /// <summary>SuperOps's GraphQL endpoint for the region. Not yet confirmed against a live EU account.</summary>
    public static string ApiUrl(string? region) => string.Equals(region, Eu, StringComparison.OrdinalIgnoreCase)
        ? "https://euapi.superops.ai/msp"
        : "https://api.superops.ai/msp";

    public static string DisplayName(string? region) => string.Equals(region, Eu, StringComparison.OrdinalIgnoreCase) ? "Europe" : "United States";
}
