namespace DotMarc.Psa.Autotask;

/// <summary>Singleton settings row for Datto Autotask, seeded by its migration like HaloPsaSettings. The API user's secret
/// lives in ISecretStore under <see cref="SecretStoreKey"/>, never on this entity; <see cref="SecretConfigured"/> is the
/// only signal that one is saved.</summary>
public sealed class AutotaskSettings
{
    public const string SecretStoreKey = "Autotask.Secret";

    /// <summary>Autotask's built-in Complete status.</summary>
    public const int CompleteStatus = 5;

    /// <summary>Autotask's built-in New status, which alert tickets start in.</summary>
    public const int NewStatus = 1;

    /// <summary>dotMARC's own Autotask API tracking identifier (integration code). Empty until Datto registers dotMARC as
    /// a vendor; until then each install enters its own through <see cref="IntegrationCodeOverride"/>.</summary>
    public const string DefaultIntegrationCode = "";

    public int Id { get; set; }
    public bool Enabled { get; set; }

    /// <summary>The API user's username, an email address. dotMARC finds the tenant's zone from it.</summary>
    public string? Username { get; set; }
    public bool SecretConfigured { get; set; }
    public string? IntegrationCodeOverride { get; set; }

    // Ticket defaults, with the names saved beside the ids so the settings page shows them before Autotask's lists
    // load. Display only: nothing sent to Autotask uses the names.
    public int? QueueId { get; set; }
    public string? QueueName { get; set; }
    public int? TicketTypeId { get; set; }
    public string? TicketTypeName { get; set; }

    /// <summary>Optional.</summary>
    public int? IssueTypeId { get; set; }
    public string? IssueTypeName { get; set; }
    public int? PriorityId { get; set; }
    public string? PriorityName { get; set; }
    public int? ClosedStatusId { get; set; } = CompleteStatus;
    public string? ClosedStatusName { get; set; } = "Complete";

    /// <summary>The tracking identifier sent to Autotask: the override if set, otherwise dotMARC's own.</summary>
    public string? EffectiveIntegrationCode => string.IsNullOrWhiteSpace(IntegrationCodeOverride)
        ? (DefaultIntegrationCode.Length > 0 ? DefaultIntegrationCode : null)
        : IntegrationCodeOverride.Trim();
}
