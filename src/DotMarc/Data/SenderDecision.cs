namespace DotMarc.Data;

/// <summary>Where a decision applies: to every domain (the baseline), to the domains of one group, or to one domain.</summary>
public enum SenderDecisionScope { Baseline, Group, Domain }

/// <summary>What a decision is about: a sending service by its catalogue name, or a single source IP (domain scope only).</summary>
public enum SenderDecisionKind { Service, Ip }

public enum SenderDecisionStatus { Authorised, UnderReview, Unauthorised }

/// <summary>A person's decision about a sender: that it should send as a domain, shouldn't, or is being looked into. It is a
/// label, never a DNS change. Having no row means Unreviewed. <see cref="GroupId"/> is set for group scope and
/// <see cref="DomainId"/> for domain scope, and neither for the baseline.</summary>
public sealed class SenderDecision
{
    public const int MaxKeyLength = 200;
    public const int MaxNoteLength = 500;

    public int Id { get; set; }
    public SenderDecisionScope Scope { get; set; }
    public int? GroupId { get; set; }
    public int? DomainId { get; set; }
    public SenderDecisionKind Kind { get; set; }
    public required string Key { get; set; }
    public SenderDecisionStatus Status { get; set; }
    public string? Note { get; set; }

    /// <summary>Who decided, as the audit log keys people (Entra object ID, else email).</summary>
    public required string DecidedBy { get; set; }

    /// <summary>Their display name at the time, so it still reads as a name after a rename.</summary>
    public required string DecidedByName { get; set; }

    public DateTimeOffset DecidedUtc { get; set; }
}
