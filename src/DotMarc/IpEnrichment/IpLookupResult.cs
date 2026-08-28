// src/DotMarc/IpEnrichment/IpLookupResult.cs
using DotMarc.Data;

namespace DotMarc.IpEnrichment;

/// <summary>The outcome of one IIpInfoLookup.LookupAsync call. Organization/Country are null
/// whenever Status isn't Ok, and may also be null on an Ok result if the RDAP response simply
/// didn't include that field.</summary>
public sealed record IpLookupResult(IpLookupStatus Status, string? Organization, string? Country);
