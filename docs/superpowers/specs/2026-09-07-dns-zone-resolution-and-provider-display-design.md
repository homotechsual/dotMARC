# DNS Zone Resolution & Provider Display Design

## Problem

dotMARC's DNS auto-push feature (Cloudflare/Azure DNS) and its `DnsProviderDetector` both assume the monitored domain name *is* the DNS zone apex. For any monitored domain that's actually a subdomain of a larger zone — e.g. `services.wrc.wales`, whose real DNS zone is `wrc.wales` — this assumption breaks in two places:

- `DnsProviderDetector.DetectAsync` queries NS records at the exact monitored domain name. A subdomain that isn't itself a delegated zone has no NS records of its own (confirmed via a live query: `services.wrc.wales` returns only an `Authority` section pointing at `wrc.wales`'s SOA, no `Answer`), so detection returns `Unknown` and the UI shows "Couldn't find a configured DNS push option - add the CNAME manually," even though the root domain is on a fully supported provider (Azure DNS, confirmed by the same live query against `wrc.wales`).
- Even if detection reported the right provider, both `AzureDnsPushProvider.FindZoneAsync` and `CloudflareDnsPushProvider.FindZoneIdAsync` do an *exact* zone-name match against the account's zone list, using whatever `Program.cs` set `DnsRecordChange.ZoneName` to — today always the full monitored domain name (or the DMARC-authorization mailbox's domain, for that one push target). Neither provider's account has a zone literally named `services.wrc.wales`, so the push would still fail with `ZoneNotFound`.

Separately, there's no way to see which DNS provider (or zone) dotMARC detected for a domain without triggering a push flow — useful at-a-glance context, and doubly so once subdomain zones are in play, since "which zone actually owns this domain's records" is no longer obvious from the domain name alone.

## Goals

- Resolve a monitored domain (or any hostname passed to detection, e.g. the DMARC-authorization mailbox's domain) to its *real* DNS zone apex, by walking up its labels until an ancestor with actual NS records is found — provider-agnostic, no authenticated API calls needed just to find the answer.
- Use that resolved zone name everywhere a push target's `ZoneName` is currently just the bare domain name, so subdomain monitoring works end-to-end for both Cloudflare and Azure DNS pushes.
- Track the detected provider and zone as a persisted, scheduled health check (like DMARC/SPF/MX), with its own recheck button.
- Show it in two places: a new "DNS provider" row in the domain detail page's health checklist, and a compact badge near the domain name header.

## Non-goals

- No change to how either push provider actually writes a record once the correct zone is found — confirmed both `AzureDnsPushProvider.PushRecordAsync`'s relative-name slicing and `CloudflareDnsPushProvider`'s absolute-name record creation already work correctly for any ancestor zone; the bug is entirely in *finding* the zone, not in what happens after.
- No Public Suffix List integration. Real DNS delegation means the first ancestor with NS records genuinely is the zone cut, for any domain that's actually registered and delegated — no need for a maintained TLD list to find it correctly.
- No change to which push providers are supported (still Cloudflare and Azure DNS only).

## Data model changes

`src/DotMarc/DnsPush/DetectedDnsProvider.cs` moves to `src/DotMarc/Data/DetectedDnsProvider.cs` — now that it's a `Domain` property, it belongs alongside `MxCheckStatus`/`SpfCheckStatus`/etc. rather than under `DnsPush/`. Gains a new first member so it has a "not yet checked" default distinct from "checked, nothing recognized":

```csharp
namespace DotMarc.Data;

/// <summary>The most recently detected DNS provider for a Domain - see
/// DotMarc.DnsPush.IDnsProviderDetector. NotChecked is listed first so it is the enum's (and the
/// database column's) default value; Unknown means the check ran but no known provider's NS suffix
/// matched (still meaningfully different from never having checked at all).</summary>
public enum DetectedDnsProvider
{
    NotChecked,
    Unknown,
    Cloudflare,
    AzureDns
}
```

`src/DotMarc/Data/Domain.cs` gains three fields, matching the established three-field-per-check shape used by every other tracked check:

```csharp
public DetectedDnsProvider DnsProvider { get; set; }
public string? DnsZone { get; set; }
public DateTimeOffset? DnsProviderCheckedUtc { get; set; }
```

`DnsZone` is a dedicated typed field rather than the usual free-text `{Check}CheckDetail` - it holds a structured value (the resolved zone name, e.g. `"wrc.wales"`) the UI displays directly, not explanatory prose. `DotMarcDbContext.cs` gets `entity.Property(d => d.DnsProvider).HasConversion<string>()`, matching every other enum column - a new column, so this DOES need an EF Core migration (unlike the null-routed-domains feature, which only added enum members to already-converted columns).

## Zone resolution mechanism

`IDnsProviderDetector.DetectAsync`'s return type changes from `Task<DetectedDnsProvider>` to `Task<DnsProviderDetectionResult>`:

```csharp
namespace DotMarc.DnsPush;

/// <summary>The result of resolving a hostname to its real DNS zone and provider - ZoneName is the
/// hostname itself when it's already an apex, or the nearest ancestor that actually has NS records
/// otherwise (see DnsProviderDetector.DetectAsync). Provider is Unknown, and ZoneName is the
/// original input hostname unchanged, when no ancestor within the walk's safety bound has a
/// recognized (or any) NS delegation.</summary>
public sealed record DnsProviderDetectionResult(DetectedDnsProvider Provider, string ZoneName);
```

`DnsProviderDetector.DetectAsync` walks from the given hostname up through its ancestor labels (`services.wrc.wales` → `wrc.wales` → `wales` → ...), querying NS at each level via the same Cloudflare DNS-over-HTTPS call it already makes, stopping at the first ancestor whose NS query returns at least one answer. That ancestor's name becomes `ZoneName`; its NS hosts are matched against the existing Cloudflare/Azure suffix tables to produce `Provider`, exactly as today. The walk queries the original hostname plus at most 5 ancestors (6 NS queries total, hard cap) — far more than any real monitored domain needs - to bound worst-case query count against a pathological input; exhausting all 6 without finding NS records returns `(Unknown, <original hostname>)`, the same fallback as today's "no NS records at all" case. An `HttpRequestException` on any single query still returns `(Unknown, <original hostname>)` immediately, without trying further ancestors - matching today's exact failure behavior for a transport-level failure.

A hostname that's already an apex (has its own NS records) resolves on the very first query - zero behavior change from today for the common case.

## Push-time integration

`Program.cs`'s `/dns-push/{provider}/callback` endpoint gets `IDnsProviderDetector` added to its handler parameters. Before building each `DnsRecordChange`, it calls `DetectAsync` fresh (not the persisted `Domain.DnsZone` from above - matching this codebase's existing convention that every push re-fetches live DNS state immediately before writing, the same reasoning `DmarcTxtLookup`/`TlsrptTxtLookup` already follow) and uses the result's `ZoneName` wherever `domain.Name` (or, for the `dmarc-auth` target, `mailboxDomain`) is currently passed as `DnsRecordChange`'s `ZoneName` argument. No other change to any `DnsRecordChange` construction.

If the freshly-detected provider doesn't match the `{provider}` route parameter the push button was built from (the page's cached chip going stale between load and click, e.g. DNS moved provider in the interim), the push is treated as `DnsPushOutcome.ZoneNotFound` - reusing the existing end-to-end popup/message flow for that outcome rather than introducing a new one.

Neither `AzureDnsPushProvider` nor `CloudflareDnsPushProvider` needs any code change: `AzureDnsPushProvider.PushRecordAsync`'s relative-name slice (`change.Name[..^(zoneName.Length + 1)]`) already produces the correct multi-label relative name for a record nested under an ancestor zone, and `CloudflareDnsPushProvider` always creates records using the absolute `change.Name`, needing `ZoneName` only to look up the right `zoneId` - both already work correctly once handed the right zone name.

## Scheduled check

New `PollingService` cycle, following the exact SPF/MX pattern: a new advisory lock key constant, a staleness query (domains whose `DnsProviderCheckedUtc` is null or older than the cycle interval), and an `internal static RunSingleDnsProviderCheckAsync(Domain domain, IDnsProviderDetector detector, CancellationToken)` method that sets `DnsProvider`/`DnsZone`/`DnsProviderCheckedUtc` from a `DetectAsync` call - reusable by a "recheck now" button exactly as `RunSingleSpfCheckAsync`/`RunSingleMxCheckAsync` already are.

## UI changes

New `DnsProviderStatusPresentation` static class (`GetColor`/`GetLabel`, matching every other `*StatusPresentation`):

```csharp
public static Color GetColor(DetectedDnsProvider provider) => provider switch
{
    DetectedDnsProvider.Cloudflare or DetectedDnsProvider.AzureDns => Color.Success,
    DetectedDnsProvider.Unknown => Color.Warning,
    _ => Color.Default
};

public static string GetLabel(DetectedDnsProvider provider) => provider switch
{
    DetectedDnsProvider.Cloudflare => "Cloudflare",
    DetectedDnsProvider.AzureDns => "Azure DNS",
    DetectedDnsProvider.Unknown => "Not recognized",
    _ => "Not checked yet"
};
```

`Unknown` gets `Color.Warning` rather than the neutral default - it specifically means no auto-push option is available, worth flagging the same way a missing record is.

`DomainDetail.razor`'s Overview tab gets a new `DomainHealthCheckRow` for "DNS provider": `StatusColor`/`StatusLabel` from the presentation class above, `CheckedUtc` from `_domain.DnsProviderCheckedUtc`, `Detail` as `_domain.DnsZone is { } zone ? $"Zone: {zone}" : null`, and a recheck button (`DomainsEdit` policy, matching the SPF/MX rows) calling the new `RunSingleDnsProviderCheckAsync`.

A compact header badge appears near the domain name at the top of the page (outside `MudTabs`, since it's a domain property rather than tab-specific content - matches this page's existing convention for domain-level vs. tab-level info): `"DNS: Azure DNS (wrc.wales)"`, shown only when `_domain.DnsProvider` is `Cloudflare` or `AzureDns` (hidden for `NotChecked`/`Unknown` - nothing to say, nothing shown, the same convention the mail-service badges already use).

**Simplification folded in:** `DomainDetail.razor` currently calls `DnsProviderDetector.DetectAsync` live three separate times, to decide which push buttons to show for the DMARC, TLSRPT, and DMARC-authorization targets. Since the result is now a persisted field loaded once in `OnInitializedAsync` alongside every other check, those three call sites read `_domain.DnsProvider` instead - three fewer redundant live DNS round-trips per page view, no behavior change to which buttons appear.

## Testing

- `DnsProviderDetectorTests.cs`: existing 6 tests updated to assert against `result.Provider` (all 6 fixtures are apex domains, so the walk-up never triggers for them - zero regression risk, confirmed by inspection). New tests: a subdomain-with-no-NS-then-parent-has-NS case reproducing the `services.wrc.wales` scenario exactly (via `FakeHttpMessageHandler.ResponseBodies` queuing an empty-answer response then a matching one), asserting the correct `(Provider, ZoneName)` pair; a two-levels-deep nested case; a domain where every ancestor within the 6-query cap has no NS records, confirming the fallback to `(Unknown, <original hostname>)` and that the walk terminates rather than looping.
- New `PollingService` cycle tests mirroring the existing SPF/MX single-check and cycle-staleness tests exactly.
- `DnsProviderStatusPresentationTests.cs`: new, following the same shape as sibling presentation tests.
- `Program.cs`'s push `/callback` endpoint has no existing test coverage today, so no regression risk there from this change; the `ZoneName` fix is verified via the `DnsProviderDetector` tests plus a manual push against a real subdomain once deployed.
- No bUnit component tests (confirmed established convention this session) - health-row and header-badge rendering verified by build plus manual browser check.

## Migration path

Unlike the null-routed-domains feature, this DOES need an EF Core migration: `Domain.DnsProvider`/`DnsZone`/`DnsProviderCheckedUtc` are new columns, not new members of an already-converted enum column. `DnsProvider`'s default value (`NotChecked`, the enum's first member) requires no backfill for existing rows - every domain simply gets its provider/zone detected on its next regularly-scheduled check, the same as any other new check added to this app.
