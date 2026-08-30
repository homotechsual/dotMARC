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
            ? countryEl.GetString()?.ToUpperInvariant()
            : null;

        string? organization = null;
        if (root.TryGetProperty("entities", out var entitiesEl) && entitiesEl.ValueKind == JsonValueKind.Array)
        {
            var entities = entitiesEl.EnumerateArray().ToList();

            // RIPE (Europe/Middle East/Central Asia) routinely lists several "registrant"-role
            // entities on one IP network, with a registry maintainer stub (e.g. "meta-mnt",
            // "AKAM1-RIPE-MNT") appearing before the actual organization. The jCard "kind"
            // property reliably distinguishes them across all five RIRs: the real organization
            // entity is kind "org", while RIPE's maintainer stubs are "individual" or have no
            // kind at all. Preference order, most specific to least:
            //   1. registrant role AND kind "org"
            //   2. any entity (regardless of role) with kind "org"
            //   3. registrant role with a usable fn (whatever kind) — the pre-existing behavior
            //   4. the first entity with any usable fn at all — the pre-existing fallback
            organization = entities
                .Where(e => HasRole(e, "registrant") && HasKind(e, "org"))
                .Select(ExtractFn)
                .FirstOrDefault(name => name is not null);

            organization ??= entities
                .Where(e => HasKind(e, "org"))
                .Select(ExtractFn)
                .FirstOrDefault(name => name is not null);

            organization ??= entities
                .Where(e => HasRole(e, "registrant"))
                .Select(ExtractFn)
                .FirstOrDefault(name => name is not null);

            organization ??= entities
                .Select(ExtractFn)
                .FirstOrDefault(name => name is not null);
        }

        return (organization, country);
    }

    /// <summary>Extracts the RDAP "ip network" object's start/end address bounds — mandatory
    /// fields on every such object per RFC 9083, unlike organization/country which are often
    /// absent. Used to cache the whole allocation block (e.g. "2a01:110::/31") a looked-up IP
    /// falls within, so every other IP in that same block resolves from the cache instead of
    /// triggering its own RDAP lookup — see IpRangeMatcher. Returns nulls unless both bounds are
    /// present: a range with only one bound would let IpRangeMatcher's containment check match
    /// everything above or below it.</summary>
    public static (string? Start, string? End) ParseRange(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var start = GetString(root, "startAddress");
        var end = GetString(root, "endAddress");

        return start is not null && end is not null ? (start, end) : (null, null);
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool HasRole(JsonElement entity, string role) =>
        entity.ValueKind == JsonValueKind.Object &&
        entity.TryGetProperty("roles", out var rolesEl) &&
        rolesEl.ValueKind == JsonValueKind.Array &&
        rolesEl.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.String && r.GetString() == role);

    /// <summary>True when the entity's jCard (vcardArray) declares a "kind" property equal to
    /// <paramref name="kind"/> — e.g. ["kind", {}, "text", "org"]. Mirrors HasRole's defensive
    /// style: any unexpected shape (missing vcardArray, no "kind" property) is simply false, never
    /// an exception.</summary>
    private static bool HasKind(JsonElement entity, string kind) =>
        ExtractVCardProperty(entity, "kind") == kind;

    /// <summary>Extracts the "fn" (formatted name) property from an RDAP entity's jCard
    /// (vcardArray), which is the standard place an entity's display name lives — e.g.
    /// ["vcard", [["version", {}, "text", "4.0"], ["fn", {}, "text", "Example Org Ltd"]]].</summary>
    private static string? ExtractFn(JsonElement entity) => ExtractVCardProperty(entity, "fn");

    /// <summary>Extracts an arbitrary named jCard property's string value from an RDAP entity's
    /// vcardArray — e.g. for propertyName "kind": ["kind", {}, "text", "org"]. Shared traversal
    /// logic for both the display-name ("fn") and entity-type ("kind") lookups, since both
    /// properties live at the same level inside vcardArray's second element.</summary>
    private static string? ExtractVCardProperty(JsonElement entity, string propertyName)
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
                property[0].GetString() == propertyName &&
                property[3].ValueKind == JsonValueKind.String)
            {
                return property[3].GetString();
            }
        }

        return null;
    }
}
