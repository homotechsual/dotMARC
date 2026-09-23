using DotMarc.Data;

namespace DotMarc.DnsPush;

/// <summary>The result of resolving a hostname to its real DNS zone and provider - see
/// DnsProviderDetector.DetectAsync. ZoneName is the hostname itself when it's already an apex, or
/// the nearest ancestor that actually has NS records otherwise. Provider is Unknown, and ZoneName is
/// the original input hostname unchanged, when no ancestor within the walk's 6-query cap has a
/// recognized (or any) NS delegation. Nameservers is the raw NS record list found at ZoneName -
/// empty when nothing was found at all - kept alongside Provider so the UI can show the actual
/// configured nameservers even when they don't match a provider we recognize.</summary>
public sealed record DnsProviderDetectionResult(DetectedDnsProvider Provider, string ZoneName, IReadOnlyList<string> Nameservers);
