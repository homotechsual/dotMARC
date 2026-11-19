namespace DotMarc.Notifications;

public enum AlertRuleScopeTarget
{
    Group,
    Tag,
    Domain
}

/// <summary>One include or exclude entry in a rule's scope. A rule with no include entries applies to every domain.</summary>
public sealed class AlertRuleScopeEntry
{
    public int Id { get; set; }
    public int AlertRuleId { get; set; }
    public AlertRuleScopeTarget Target { get; set; }
    public int TargetId { get; set; }
    public bool Exclude { get; set; }
}

/// <summary>A configured alert: a built-in kind (an <see cref="AlertTypes"/> key) with its own settings, scope and severity.
/// Rules of a kind are processed in <see cref="Position"/> order. See docs/superpowers/specs/2026-11-19-alert-rules-design.md.</summary>
public sealed class AlertRule
{
    public int Id { get; set; }

    /// <summary>An <see cref="AlertTypes"/> key. Fixed once the rule is created.</summary>
    public required string Kind { get; set; }

    public required string Name { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>"Info" or "Warning".</summary>
    public required string Severity { get; set; }

    /// <summary>Processing order within the kind, lowest first.</summary>
    public int Position { get; set; }

    /// <summary>When true, rules after this one are skipped for the domains this rule applies to.</summary>
    public bool StopProcessing { get; set; } = true;

    /// <summary>Set only on a default pack rule, and equal to its <see cref="Kind"/>. Lets seeding find it again.</summary>
    public string? DefaultKey { get; set; }

    /// <summary>Null uses the global cooldown.</summary>
    public int? CooldownMinutes { get; set; }

    /// <summary>Null uses the global setting where the kind has one.</summary>
    public int? AutoCloseDays { get; set; }

    /// <summary>{"key":"value"} overrides of the kind's parameters. Null means every parameter is inherited from the global settings.</summary>
    public string? ParametersJson { get; set; }

    public List<AlertRuleScopeEntry> Scope { get; set; } = [];
}
