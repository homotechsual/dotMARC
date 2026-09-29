using System.Text.Json;

namespace DotMarc.Audit;

/// <summary>How <see cref="AuditEntry.Changes"/> is written to its jsonb column: camelCase, so the stored shape is
/// <c>{ "field", "old", "new", "secret" }</c> as the spec describes.</summary>
internal static class AuditJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
