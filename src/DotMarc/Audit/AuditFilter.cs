using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace DotMarc.Audit;

/// <summary>What the Audit log page is showing. Dates are UTC days, and <see cref="To"/> includes the whole day.
/// It travels in the export link's query string, so the export matches the page exactly.</summary>
public sealed record AuditFilter
{
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public AuditEntryKind? Kind { get; init; }
    public bool IncludePageViews { get; init; }
    public string? Who { get; init; }
    public string? Action { get; init; }
    public string? Target { get; init; }
    public string? Summary { get; init; }

    private const string DateFormat = "yyyy-MM-dd";

    public string ToQueryString()
    {
        var pairs = new List<KeyValuePair<string, string?>>();
        if (From is { } from) pairs.Add(new("from", from.ToString(DateFormat, CultureInfo.InvariantCulture)));
        if (To is { } to) pairs.Add(new("to", to.ToString(DateFormat, CultureInfo.InvariantCulture)));
        if (Kind is { } kind) pairs.Add(new("kind", kind.ToString()));
        if (IncludePageViews) pairs.Add(new("pageViews", "true"));
        if (!string.IsNullOrWhiteSpace(Who)) pairs.Add(new("who", Who));
        if (!string.IsNullOrWhiteSpace(Action)) pairs.Add(new("action", Action));
        if (!string.IsNullOrWhiteSpace(Target)) pairs.Add(new("target", Target));
        if (!string.IsNullOrWhiteSpace(Summary)) pairs.Add(new("summary", Summary));
        return QueryString.Create(pairs).ToUriComponent().TrimStart('?');
    }

    public static AuditFilter FromQuery(IQueryCollection query) => new()
    {
        From = ParseDate(query["from"]),
        To = ParseDate(query["to"]),
        Kind = Enum.TryParse<AuditEntryKind>(query["kind"].ToString(), ignoreCase: true, out var kind) ? kind : null,
        IncludePageViews = query["pageViews"] == "true",
        Who = NullIfBlank(query["who"]),
        Action = NullIfBlank(query["action"]),
        Target = NullIfBlank(query["target"]),
        Summary = NullIfBlank(query["summary"]),
    };

    /// <summary>A short description for the audit.exported entry, such as <c>from 2026-09-01, who contains "sam"</c>.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (From is { } from) parts.Add($"from {from.ToString(DateFormat, CultureInfo.InvariantCulture)}");
        if (To is { } to) parts.Add($"to {to.ToString(DateFormat, CultureInfo.InvariantCulture)}");
        if (Kind is { } kind) parts.Add($"kind {kind}");
        if (IncludePageViews) parts.Add("including page views");
        if (!string.IsNullOrWhiteSpace(Who)) parts.Add($"who contains \"{Who}\"");
        if (!string.IsNullOrWhiteSpace(Action)) parts.Add($"action {Action}");
        if (!string.IsNullOrWhiteSpace(Target)) parts.Add($"target contains \"{Target}\"");
        if (!string.IsNullOrWhiteSpace(Summary)) parts.Add($"summary contains \"{Summary}\"");
        return parts.Count == 0 ? "everything" : string.Join(", ", parts);
    }

    private static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
