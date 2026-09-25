using System.Text;
using System.Text.Json;

namespace DotMarc.Notifications;

/// <summary>What could be read from one webhook body. <see cref="Shape"/> lists the field names Halo sent,
/// never their values, so it can be shown and logged when the body wasn't understood.</summary>
public sealed record HaloWebhookReading(int? TicketId, int? StatusId, string Shape);

/// <summary>Finds the ticket and its status in a HaloPSA webhook body. Halo lets an admin pick the payload
/// (a small object, the full object, the event alone, or a custom one) and doesn't document field names, so
/// this looks in the places each is likely to put them: the top level, and an <c>object</c>, <c>ticket</c>
/// or <c>data</c> wrapper. A payload with no status (the event alone) yields a ticket id only.</summary>
public static class HaloWebhookPayloadReader
{
    private const int MaxShapeLength = 300;
    private static readonly string[] Wrappers = ["object", "ticket", "data"];
    private static readonly string[] TicketIdNames = ["ticket_id", "ticketid", "object_id", "objectid"];
    private static readonly string[] StatusIdNames = ["status_id", "statusid", "new_status", "newstatus"];

    public static HaloWebhookReading Read(JsonElement root)
    {
        var scopes = new List<JsonElement>();
        if (root.ValueKind == JsonValueKind.Object)
        {
            scopes.Add(root);
            foreach (var wrapper in Wrappers)
            {
                if (HaloJson.TryGetProperty(root, wrapper, out var inner) && inner.ValueKind == JsonValueKind.Object)
                {
                    scopes.Add(inner);
                }
            }
        }

        // A bare "id" is the last resort: at the top level Halo makes it a GUID, which is refused as a number.
        var ticketId = FindNumber(scopes, TicketIdNames) ?? FindNumber(scopes, ["id"]);
        var statusId = FindNumber(scopes, StatusIdNames) ?? FindNestedStatusId(scopes);
        return new HaloWebhookReading(ticketId, statusId, DescribeShape(root));
    }

    private static int? FindNumber(List<JsonElement> scopes, string[] names)
    {
        foreach (var name in names)
        {
            foreach (var scope in scopes)
            {
                if (HaloJson.TryGetProperty(scope, name, out var value) && HaloJson.TryGetWholeNumber(value, out var number))
                {
                    return number;
                }
            }
        }

        return null;
    }

    // "status": { "id": 9, ... }
    private static int? FindNestedStatusId(List<JsonElement> scopes)
    {
        foreach (var scope in scopes)
        {
            if (HaloJson.TryGetProperty(scope, "status", out var status)
                && HaloJson.TryGetProperty(status, "id", out var id)
                && HaloJson.TryGetWholeNumber(id, out var number))
            {
                return number;
            }
        }

        return null;
    }

    /// <summary>For example <c>id, event, object_id, object { id, summary }</c>. Names only, two levels deep.</summary>
    public static string DescribeShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return $"a JSON {root.ValueKind.ToString().ToLowerInvariant()}, not an object";
        }

        var text = new StringBuilder();
        AppendNames(text, root, depth: 0);
        return text.Length <= MaxShapeLength ? text.ToString() : text.ToString(0, MaxShapeLength) + "...";
    }

    private static void AppendNames(StringBuilder text, JsonElement element, int depth)
    {
        var first = true;
        foreach (var property in element.EnumerateObject())
        {
            if (!first)
            {
                text.Append(", ");
            }

            first = false;
            text.Append(property.Name);
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                text.Append("[]");
            }
            else if (property.Value.ValueKind == JsonValueKind.Object && depth < 1)
            {
                text.Append(" { ");
                AppendNames(text, property.Value, depth + 1);
                text.Append(" }");
            }
            else if (property.Value.ValueKind == JsonValueKind.Object)
            {
                text.Append(" {...}");
            }
        }
    }
}
