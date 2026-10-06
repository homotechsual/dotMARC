namespace DotMarc.Psa.ConnectWise;

/// <summary>Singleton settings row for ConnectWise PSA (Manage), seeded by its migration like HaloPsaSettings. The API
/// member's private key lives in ISecretStore under <see cref="PrivateKeySecretKey"/>, never on this entity;
/// <see cref="PrivateKeyConfigured"/> is the only signal that one is saved.</summary>
public sealed class ConnectWiseSettings
{
    public const string PrivateKeySecretKey = "ConnectWise.PrivateKey";

    /// <summary>dotMARC's own ConnectWise developer client ID, registered at developer.connectwise.com. ConnectWise
    /// requires one on every request; an install can use its own instead through <see cref="ClientIdOverride"/>.</summary>
    public const string DefaultClientId = "879a8ee1-38b2-400b-91db-960494366b95";

    public int Id { get; set; }
    public bool Enabled { get; set; }

    /// <summary>The API host only, such as api-eu.myconnectwise.net.</summary>
    public string? SiteUrl { get; set; }

    /// <summary>The company ID the API member signs in with.</summary>
    public string? CompanyId { get; set; }
    public string? PublicKey { get; set; }
    public bool PrivateKeyConfigured { get; set; }
    public string? ClientIdOverride { get; set; }

    // Ticket defaults, with the names saved beside the ids so the settings page shows them before ConnectWise's lists
    // load. Display only: nothing sent to ConnectWise uses the names.
    public int? BoardId { get; set; }
    public string? BoardName { get; set; }

    /// <summary>The status new tickets start in. Statuses belong to a board, so this is one of the board's.</summary>
    public int? StatusId { get; set; }
    public string? StatusName { get; set; }

    /// <summary>Optional: the board's ticket type.</summary>
    public int? TypeId { get; set; }
    public string? TypeName { get; set; }
    public int? PriorityId { get; set; }
    public string? PriorityName { get; set; }
    public int? ClosedStatusId { get; set; }
    public string? ClosedStatusName { get; set; }

    /// <summary>The client ID sent to ConnectWise: the override if set, otherwise dotMARC's own.</summary>
    public string? EffectiveClientId => string.IsNullOrWhiteSpace(ClientIdOverride)
        ? (DefaultClientId.Length > 0 ? DefaultClientId : null)
        : ClientIdOverride.Trim();
}
