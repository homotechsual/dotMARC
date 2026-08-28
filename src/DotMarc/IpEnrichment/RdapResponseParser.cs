// src/DotMarc/IpEnrichment/RdapResponseParser.cs
using System.Text.Json;

namespace DotMarc.IpEnrichment;

/// <summary>Pure parser for one RDAP "ip network" JSON response (RFC 9083) — extracts the
/// registrant organization's display name and the registry country, defensively: RDAP's exact
/// shape varies between registries (a top-level "country" isn't always present; the entity
/// holding ownership info isn't always tagged "registrant"), so a missing field produces a null,
/// never an exception. No I/O — RdapIpInfoLookup is the thin adapter that fetches the JSON this
/// parses, matching this codebase's "pure core, thin I/O adapter" convention (see
/// DomainStatistics, DmarcReportParser).</summary>
public static class RdapResponseParser
{
    public static (string? Organization, string? Country) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var country = root.TryGetProperty("country", out var countryEl) && countryEl.ValueKind == JsonValueKind.String
            ? countryEl.GetString()
            : null;

        string? organization = null;
        if (root.TryGetProperty("entities", out var entitiesEl) && entitiesEl.ValueKind == JsonValueKind.Array)
        {
            var entities = entitiesEl.EnumerateArray().ToList();

            organization = entities
                .Where(e => HasRole(e, "registrant"))
                .Select(ExtractFn)
                .FirstOrDefault(name => name is not null);

            organization ??= entities
                .Select(ExtractFn)
                .FirstOrDefault(name => name is not null);
        }

        return (organization, country);
    }

    private static bool HasRole(JsonElement entity, string role) =>
        entity.ValueKind == JsonValueKind.Object &&
        entity.TryGetProperty("roles", out var rolesEl) &&
        rolesEl.ValueKind == JsonValueKind.Array &&
        rolesEl.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.String && r.GetString() == role);

    /// <summary>Extracts the "fn" (formatted name) property from an RDAP entity's jCard
    /// (vcardArray), which is the standard place an entity's display name lives — e.g.
    /// ["vcard", [["version", {}, "text", "4.0"], ["fn", {}, "text", "Example Org Ltd"]]].</summary>
    private static string? ExtractFn(JsonElement entity)
    {
        if (entity.ValueKind != JsonValueKind.Object ||
            !entity.TryGetProperty("vcardArray", out var vcardArrayEl) ||
            vcardArrayEl.ValueKind != JsonValueKind.Array ||
            vcardArrayEl.GetArrayLength() < 2)
        {
            return null;
        }

        var properties = vcardArrayEl[1];
        if (properties.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var property in properties.EnumerateArray())
        {
            if (property.ValueKind == JsonValueKind.Array &&
                property.GetArrayLength() >= 4 &&
                property[0].ValueKind == JsonValueKind.String &&
                property[0].GetString() == "fn" &&
                property[3].ValueKind == JsonValueKind.String)
            {
                return property[3].GetString();
            }
        }

        return null;
    }
}
