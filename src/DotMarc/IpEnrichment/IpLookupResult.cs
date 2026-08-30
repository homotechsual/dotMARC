// src/DotMarc/IpEnrichment/IpLookupResult.cs
using DotMarc.Data;

namespace DotMarc.IpEnrichment;

/// <summary>The outcome of one IIpInfoLookup.LookupAsync call. Organization/Country are null
/// whenever Status isn't Ok, and may also be null on an Ok result if the RDAP response simply
/// didn't include that field. RangeStart/RangeEnd are the RDAP response's start/end address
/// bounds — present whenever Status is Ok (they're mandatory RDAP fields, unlike
/// Organization/Country) — used to cache the whole allocation block a looked-up IP falls within;
/// see IpRangeMatcher.</summary>
public sealed record IpLookupResult(IpLookupStatus Status, string? Organization, string? Country, string? RangeStart = null, string? RangeEnd = null);
