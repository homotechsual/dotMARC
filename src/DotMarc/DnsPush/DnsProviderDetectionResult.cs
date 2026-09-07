namespace DotMarc.DnsPush;

/// <summary>The result of resolving a hostname to its real DNS zone and provider - see
/// DnsProviderDetector.DetectAsync. ZoneName is the hostname itself when it's already an apex, or
/// the nearest ancestor that actually has NS records otherwise. Provider is Unknown, and ZoneName is
/// the original input hostname unchanged, when no ancestor within the walk's 6-query cap has a
/// recognized (or any) NS delegation.</summary>
public sealed record DnsProviderDetectionResult(DotMarc.Data.DetectedDnsProvider Provider, string ZoneName);
