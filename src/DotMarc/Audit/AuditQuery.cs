using Microsoft.EntityFrameworkCore;

namespace DotMarc.Audit;

/// <summary>Applies an <see cref="AuditFilter"/> in the database, for the page's paging and for export.</summary>
public static class AuditQuery
{
    private const string LikeEscape = "\\";

    public static IQueryable<AuditEntry> Apply(IQueryable<AuditEntry> entries, AuditFilter filter)
    {
        if (filter.From is { } from)
        {
            var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            entries = entries.Where(entry => entry.OccurredUtc >= start);
        }

        if (filter.To is { } to)
        {
            // The whole To day: everything before midnight at its end.
            var endExclusive = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            entries = entries.Where(entry => entry.OccurredUtc < endExclusive);
        }

        if (filter.Kind is { } kind)
        {
            entries = entries.Where(entry => entry.Kind == kind);
        }
        else if (!filter.IncludePageViews)
        {
            entries = entries.Where(entry => entry.Kind != AuditEntryKind.PageView);
        }

        if (ContainsPattern(filter.Who) is { } whoPattern)
        {
            entries = entries.Where(entry => EF.Functions.ILike(entry.ActorName, whoPattern, LikeEscape)
                || (entry.ActorEmail != null && EF.Functions.ILike(entry.ActorEmail, whoPattern, LikeEscape)));
        }

        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            entries = entries.Where(entry => entry.Action == filter.Action);
        }

        if (ContainsPattern(filter.Target) is { } targetPattern)
        {
            entries = entries.Where(entry => entry.TargetName != null && EF.Functions.ILike(entry.TargetName, targetPattern, LikeEscape));
        }

        if (ContainsPattern(filter.Summary) is { } summaryPattern)
        {
            entries = entries.Where(entry => EF.Functions.ILike(entry.Summary, summaryPattern, LikeEscape));
        }

        return entries;
    }

    /// <summary>A case-insensitive "contains" pattern, with LIKE's own wildcards escaped so "100%" means the text
    /// "100%", not "100 then anything".</summary>
    private static string? ContainsPattern(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
