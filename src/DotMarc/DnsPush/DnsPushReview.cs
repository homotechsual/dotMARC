namespace DotMarc.DnsPush;

/// <summary>One DNS record as the review screen lists it.</summary>
public sealed record DnsReviewRecord(string Type, string Name, string Content);

/// <summary>What a push would do, laid out for a person to check before they sign in to their DNS provider: the records
/// that will be added, and the existing records that will be removed or replaced to make room for them. Built from a
/// <see cref="DnsChangePlan"/>, so it is the same for every push target and every DNS provider.</summary>
public sealed record DnsPushReview(
    string? ZoneName,
    IReadOnlyList<DnsReviewRecord> Additions,
    IReadOnlyList<DnsReviewRecord> Removals,
    IReadOnlyList<DnsReviewRecord> LostDelegations)
{
    /// <summary>What the push repairs, in plain words; empty when it only adds or updates records.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>True when DNS already holds everything the plan would write, so there is nothing to push.</summary>
    public bool NothingToChange => Additions.Count == 0;

    public static DnsPushReview From(DnsChangePlan plan)
    {
        var additions = new List<DnsReviewRecord>();
        var removals = new List<DnsReviewRecord>();
        var lostDelegations = new List<DnsReviewRecord>();

        foreach (var change in plan.Changes)
        {
            switch (change.Kind)
            {
                case DnsRecordChangeKind.Create:
                    additions.Add(new(change.RecordType, change.Name, change.DesiredValue));
                    break;

                case DnsRecordChangeKind.Merge:
                    if (!SameValue(change.RecordType, change.ExistingValue, change.DesiredValue))
                    {
                        additions.Add(new(change.RecordType, change.Name, change.DesiredValue));
                        if (change.ExistingValue is not null)
                        {
                            removals.Add(new(change.RecordType, change.Name, change.ExistingValue));
                        }
                    }

                    break;

                case DnsRecordChangeKind.Replace:
                    var existingType = change.ExistingRecordType ?? change.RecordType;
                    additions.Add(new(change.RecordType, change.Name, change.DesiredValue));
                    if (change.ExistingValue is not null)
                    {
                        var removed = new DnsReviewRecord(existingType, change.Name, change.ExistingValue);
                        removals.Add(removed);

                        // A CNAME that points somewhere else is usually a third party managing the name, and replacing it ends that.
                        if (existingType == "CNAME" && change.RecordType != "CNAME")
                        {
                            lostDelegations.Add(removed);
                        }
                    }

                    break;

                case DnsRecordChangeKind.ReplaceTxtValues:
                    var toRemove = change.ValuesToRemove ?? [];
                    if (toRemove.Contains(change.DesiredValue, StringComparer.Ordinal) && toRemove.Count == 1)
                    {
                        break;
                    }

                    additions.Add(new(change.RecordType, change.Name, change.DesiredValue));
                    removals.AddRange(toRemove.Where(value => value != change.DesiredValue).Select(value => new DnsReviewRecord(change.RecordType, change.Name, value)));
                    break;
            }
        }

        return new DnsPushReview(plan.Changes.Select(change => change.ZoneName).FirstOrDefault(), additions, removals, lostDelegations) { Notes = plan.Notes };
    }

    /// <summary>The record name as most DNS providers' "Host" or "Name" box wants it: relative to the zone, with "@" for the
    /// zone's own apex. Returns <paramref name="name"/> unchanged when it is not inside <paramref name="zoneName"/>.</summary>
    public static string HostWithinZone(string? zoneName, string name)
    {
        if (string.IsNullOrWhiteSpace(zoneName))
        {
            return name;
        }

        var zone = zoneName.TrimEnd('.');
        var fullName = name.TrimEnd('.');
        if (string.Equals(fullName, zone, StringComparison.OrdinalIgnoreCase))
        {
            return "@";
        }

        return fullName.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase)
            ? fullName[..^(zone.Length + 1)]
            : name;
    }

    /// <summary>The records as tab-separated lines (type, name, value), for pasting into a ticket or a spreadsheet.</summary>
    public static string ToClipboardText(IEnumerable<DnsReviewRecord> records) =>
        string.Join(Environment.NewLine, records.Select(record => $"{record.Type}\t{record.Name}\t{record.Content}"));

    private static bool SameValue(string recordType, string? existing, string desired) =>
        existing is not null && string.Equals(existing, desired, recordType == "CNAME" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
