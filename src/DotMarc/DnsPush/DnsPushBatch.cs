namespace DotMarc.DnsPush;

/// <summary>Planned until its person signs in to the provider, Running while the pushes happen, then Completed. Interrupted is a batch
/// that was Running when dotMARC stopped, whose provider session was lost with the process. Expired is one nobody signed in for in time.</summary>
public enum DnsPushBatchStatus { Planned, Running, Completed, Interrupted, Expired }

/// <summary>Where one domain in a batch has got to. Planned, NothingToChange and Refused are the review's verdicts. The rest are what
/// happened when the batch ran: ChangedSinceReview means the plan worked out at push time was not the one reviewed, so nothing was pushed.
/// FailedAfterChange is a failure that may have changed the zone (a replace that failed after deleting the old record, or an SPF record left
/// duplicated), which needs a person to look.</summary>
public enum DnsPushBatchDomainStatus { Planned, NothingToChange, Refused, Pushed, ChangedSinceReview, ZoneNotFound, Failed, FailedAfterChange, NotAttempted }

/// <summary>Several domains on one DNS provider, pushed one record target with one sign-in. The review is stored here, so only the batch id
/// has to travel through the provider's OAuth sign-in. Holds no secret: the provider session that does the pushing exists only in memory.</summary>
public sealed class DnsPushBatch
{
    public Guid Id { get; set; }

    /// <summary>The person who started it, as <c>DashboardPreferencesStore.UserKeyOf</c> keys people. Only they can sign in to run it.</summary>
    public required string StartedBy { get; set; }

    public required string Provider { get; set; }
    public required string Target { get; set; }
    public DnsPushBatchStatus Status { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>How long the review can be signed in to. After this the batch can't be run.</summary>
    public DateTimeOffset ExpiresUtc { get; set; }

    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }

    public List<DnsPushBatchDomain> Domains { get; set; } = [];
}

/// <summary>One domain in a batch. The domain's name is kept here so the row still reads sensibly if the domain is deleted afterwards, which is
/// why there is no foreign key to the domain.</summary>
public sealed class DnsPushBatchDomain
{
    public int Id { get; set; }
    public Guid BatchId { get; set; }
    public int DomainId { get; set; }
    public string DomainName { get; set; } = "";
    public int Position { get; set; }

    /// <summary>Whether this domain will be pushed. Only a Planned domain can be included, and a person can untick any of them.</summary>
    public bool Included { get; set; }

    public DnsPushBatchDomainStatus Status { get; set; }

    /// <summary>The fingerprint of the plan that was reviewed (<see cref="DnsPlanFingerprint"/>), for a Planned domain. Compared with the plan
    /// worked out again at push time, so nothing that wasn't reviewed is pushed.</summary>
    public string? PlanFingerprint { get; set; }
    public string? ReviewJson { get; set; }

    /// <summary>The refusal flag, or a short description of what went wrong. Never a secret.</summary>
    public string? Detail { get; set; }

    public DateTimeOffset? FinishedUtc { get; set; }
}

/// <summary>The limits a batch works within.</summary>
public static class DnsPushBatchLimits
{
    public const int MaxDomains = 50;
    public static readonly TimeSpan ReviewWindow = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>A pause between one domain's push and the next, so a provider's rate limit isn't hit hard.</summary>
    public static readonly TimeSpan PauseBetweenDomains = TimeSpan.FromMilliseconds(250);
}

/// <summary>A batch can't be made or changed, with a sentence that says why in words a person can act on.</summary>
public sealed class DnsPushBatchException(string message) : Exception(message);
