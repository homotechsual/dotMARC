# Google Cloud DNS Push Provider Design

## Problem

dotMARC's DNS auto-push currently supports two providers (Cloudflare, Azure DNS), each following the same shape: a user clicks "Push via your DNS provider," authorizes via OAuth using their own account, and dotMARC writes the record using that account's own permissions - dotMARC never holds a standing credential of its own. Google Cloud DNS is a common third option, especially for domains belonging to businesses running infrastructure on GCP, and isn't supported today; a domain hosted there shows "Couldn't find a configured DNS push option - add the record manually" regardless of how well-configured the deployment otherwise is.

## Goals

- Add Google Cloud DNS as a third `IDnsPushProvider`, matching the existing "signed-in user's own permissions govern the push, nothing about the OAuth token is ever persisted" trust model exactly.
- Detect Google Cloud DNS via its default NS suffix, alongside the existing Cloudflare/Azure detection.
- Find the right zone to push to by enumerating every GCP project the authorizing user can see - no admin-configured Project ID, for the same reason `AzureDnsSettings.TenantId` was removed: a single hard-coded value breaks the per-customer delegation this whole feature exists for.

## Non-goals

- No support for the legacy Google Domains registrar product (sold to Squarespace in 2023). Its DNS zones share the same `.googledomains.com` name-server suffix as genuine Google Cloud DNS zones (see Detection below) but have no Google Cloud Platform project behind them and no API path this feature can reach - a push attempt against one fails cleanly as `ZoneNotFound`, the same outcome any other unmatched zone already produces today.
- No private/internal Cloud DNS zones - only public zones can ever be the real authority for a domain's public NS records, so only public zones are ever searched.
- No DNSSEC configuration.
- No change to `CloudflareDnsPushProvider.cs` or `AzureDnsPushProvider.cs` - this is a new, independent implementation of the existing `IDnsPushProvider` interface, not a refactor of the other two.

## Data model changes

New `src/DotMarc/Notifications/GoogleCloudDnsSettings.cs`, structurally identical to `CloudflareDnsSettings`:
```csharp
namespace DotMarc.Notifications;

/// <summary>Singleton settings row for Google Cloud DNS push, same pattern as
/// CloudflareDnsSettings/AzureDnsSettings - the client secret lives in ISecretStore under
/// SecretStoreKey, never on this entity.</summary>
public sealed class GoogleCloudDnsSettings
{
    public const string SecretStoreKey = "GoogleCloudDns.ClientSecret";

    public int Id { get; set; }
    public string? ClientId { get; set; }
    public bool ClientSecretConfigured { get; set; }
}
```

New `src/DotMarc/Notifications/GoogleCloudDnsSettingsService.cs`, identical shape to `CloudflareDnsSettingsService`:
```csharp
public static class GoogleCloudDnsSettingsService
{
    public static Task<GoogleCloudDnsSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.GoogleCloudDnsSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, ISecretStore secretStore, GoogleCloudDnsSettings updated, string? newClientSecret, CancellationToken cancellationToken = default)
    {
        var existing = await context.GoogleCloudDnsSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.ClientId = updated.ClientId;

        if (!string.IsNullOrWhiteSpace(newClientSecret))
        {
            await secretStore.SetSecretAsync(GoogleCloudDnsSettings.SecretStoreKey, newClientSecret, cancellationToken).ConfigureAwait(false);
            existing.ClientSecretConfigured = true;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

`DotMarcDbContext.cs` gains `DbSet<GoogleCloudDnsSettings> GoogleCloudDnsSettings` plus a seed row (`modelBuilder.Entity<GoogleCloudDnsSettings>().HasData(new GoogleCloudDnsSettings { Id = 1 })`), matching `AzureDnsSettings`'s exact registration - this needs a migration (new table, unlike the enum-only additions elsewhere in this codebase).

`src/DotMarc/Data/DetectedDnsProvider.cs` gains a new member after `AzureDns`:
```csharp
public enum DetectedDnsProvider
{
    NotChecked,
    Unknown,
    Cloudflare,
    AzureDns,
    GoogleCloudDns
}
```
No migration needed for this part - it's a new string value in the already-`HasConversion<string>()`-configured `Domain.DnsProvider` column, same precedent as `MxCheckStatus.NullMx`/`SpfCheckStatus.NullSpf`.

## Detection

`src/DotMarc/DnsPush/DnsProviderDetector.cs`'s suffix tables gain one new entry:
```csharp
private static readonly string[] GoogleCloudDnsNsSuffixes = [".googledomains.com"];
```
checked in the existing walk-up loop exactly like the Cloudflare/Azure suffix arrays - no change to the walk-up algorithm itself, just one more array to check per candidate.

**Known ambiguity, accepted:** `.googledomains.com` is also the name-server suffix the legacy Google Domains registrar (now Squarespace-operated) used, and still uses for zones that haven't been migrated. There is no way to distinguish "a real Cloud DNS zone this app can push to" from "a legacy Google Domains zone with no GCP project behind it" using NS records alone. A push against the latter fails cleanly as `ZoneNotFound` once zone discovery (below) searches every accessible project and finds nothing - the same outcome any other unmatched zone produces, not a new failure mode.

`src/DotMarc/Reporting/DnsProviderStatusPresentation.cs` and `src/DotMarc/DnsPush/DetectedDnsProviderExtensions.cs`'s `ToProviderKey()` each gain one new switch arm - `GoogleCloudDns => Color.Success` / `"Google Cloud DNS"` / `"google-cloud-dns"` respectively, following the exact pattern their Cloudflare/AzureDns arms already use. No other file needs a matching-string update: every push-button handler and `Program.cs`'s zone-resolution guard already read through `ToProviderKey()`, which is exactly the centralization the final review of the DNS zone resolution work recommended and delivered.

## OAuth flow

New `src/DotMarc/DnsPush/GoogleCloudDnsPushProvider.cs` implementing `IDnsPushProvider`, `ProviderKey => "google-cloud-dns"`. Same "nothing about the access token is ever persisted, exists only as a local variable for the duration of one push" contract as the other two providers.

`BuildAuthorizationUrlAsync` targets `https://accounts.google.com/o/oauth2/v2/auth` with PKCE (reusing the existing `PkceGenerator.Generate()` helper unchanged) and two scopes:
- `https://www.googleapis.com/auth/ndev.clouddns.readwrite` - read/write access to Cloud DNS.
- `https://www.googleapis.com/auth/cloudplatformprojects.readonly` - the narrowest scope the Resource Manager `projects.list` endpoint accepts for listing accessible projects (confirmed directly against Google's own REST API reference for that method, which lists it as one of four valid scopes alongside the broader `cloud-platform`/`cloud-platform.read-only`/`cloudplatformprojects`).

No `access_type=offline` or `prompt=consent` - this flow never requests or needs a refresh token.

`ExchangeAndPushAsync` exchanges the authorization code at `https://oauth2.googleapis.com/token` via a plain form-encoded `HttpClient` POST (`grant_type=authorization_code`, `code`, `redirect_uri`, `client_id`, `client_secret`, `code_verifier`) - no MSAL equivalent is needed for Google's token endpoint (MSAL is Microsoft-specific); this is architecturally closer to how `CloudflareDnsPushProvider.ExchangeCodeForTokenAsync` already does its own manual token exchange than to Azure's MSAL-based approach.

**Known operational constraint, not a design gap:** the Cloud DNS write scope is very likely classified by Google as a "sensitive" OAuth scope, meaning production use beyond a small allowlisted set of test users will likely require submitting the OAuth consent screen for Google's review (a manual step in Google Cloud Console, turnaround measured in days) before real customers outside that allowlist can complete the flow. This doesn't block building the feature - it's a one-time deployment-operator step, the same category of constraint as the Entra multitenant setting Azure DNS push already needs, and belongs in the same setup documentation once this ships.

## Zone discovery

Mirrors `AzureDnsPushProvider.FindZoneAsync`'s exact shape:
```csharp
private static async Task<(string ProjectId, string ManagedZoneName)?> FindZoneAsync(HttpClient http, string accessToken, string zoneName, CancellationToken cancellationToken)
{
    await foreach (var projectId in ListAccessibleProjectIdsAsync(http, accessToken, cancellationToken))
    {
        var managedZoneName = await FindManagedZoneAsync(http, accessToken, projectId, zoneName, cancellationToken).ConfigureAwait(false);
        if (managedZoneName is not null)
        {
            return (projectId, managedZoneName);
        }
    }
    return null;
}
```
`ListAccessibleProjectIdsAsync` pages through `GET https://cloudresourcemanager.googleapis.com/v1/projects` (the Resource Manager API's project-listing endpoint, paginated via its `nextPageToken`). `FindManagedZoneAsync` calls `GET https://dns.googleapis.com/dns/v1/projects/{projectId}/managedZones` for that one project and matches by the zone's `dnsName` field (the domain name with a trailing dot, e.g. `"wrc.wales."`) against `zoneName` (also given a trailing dot before comparing). First match across the whole search wins, same as Azure's first-subscription-first-zone-wins behavior. No match across every accessible project is `DnsPushOutcome.ZoneNotFound`, message naming "the Google account you authorized" rather than "any subscription you authorized."

## Record push mechanics

Google Cloud DNS has no per-record CRUD endpoint - every mutation is a `Change` resource (`POST https://dns.googleapis.com/dns/v1/projects/{projectId}/managedZones/{managedZoneName}/changes`) with `additions`/`deletions` arrays of full `ResourceRecordSet` objects (`{ name, type, ttl, rrdatas }`), applied atomically by Google. This collapses `Create`/`Merge`/`Replace` into one underlying operation shape instead of the three separate code paths Cloudflare/Azure each need:

- **Create** (`DnsRecordChangeKind.Create`): `GET .../rrsets?name={fqdn}.&type={type}` first to confirm nothing already exists there (same "don't silently overwrite" guard `AzureDnsPushProvider.PushRecordAsync` already applies), then one `Change` with `additions: [newRrset]` only.
- **Merge** (`DnsRecordChangeKind.Merge`): `GET` the current rrset first - required, since Cloud DNS's Change API needs the *exact* existing record data to delete it, not just a name/type pair - then one `Change` with `deletions: [existingRrset]` and `additions: [newRrset]`.
- **Replace** (`DnsRecordChangeKind.Replace`, the third-party CNAME-delegation case): the same shape as Merge, just spanning two record types in one `Change` - `deletions: [existingCnameRrset]`, `additions: [newTxtRrset]`.

**Because the `Change` API is transactional, the `DnsPushOutcome.ReplaceFailedAfterDelete` failure window structurally cannot occur for this provider** the way it can for Cloudflare/Azure. Those two need that outcome because their delete-then-create is two separate HTTP calls with a real gap where the old record is gone and the new one hasn't landed; Google's is one atomic call - it either fully applies or doesn't apply at all. `GoogleCloudDnsPushProvider` still returns `ReplaceFailedAfterDelete` from `DnsPushOutcome`'s shared vocabulary if the `Change` call itself fails outright (keeping the outcome type meaningful across all three providers), but there is no multi-step partial-failure state to detect and report separately the way the other two providers' `ReplaceRecordAsync` methods have to.

TXT `rrdatas` entries are literal zone-file text and must be quoted (an unquoted value is non-conformant, same reasoning as `CloudflareDnsPushProvider.BuildContent`'s existing comment) - a matching quoting helper is ported, not reinvented.

## UI and wiring

`src/DotMarc/Components/Pages/DnsPushSettings.razor` gains a third `MudPaper` section, "Google Cloud DNS," with the same two-field (Client ID, Client secret) layout Cloudflare's section already uses - no project field, since zone discovery needs no admin-configured value.

`src/DotMarc/Program.cs` gains one new DI registration block, matching the Cloudflare/Azure pattern exactly:
```csharp
builder.Services.AddHttpClient<DotMarc.DnsPush.GoogleCloudDnsPushProvider>();
builder.Services.AddSingleton<DotMarc.DnsPush.IDnsPushProvider>(sp => sp.GetRequiredService<DotMarc.DnsPush.GoogleCloudDnsPushProvider>());
```

**Nothing else changes.** The `/dns-push/{provider}/start` and `/dns-push/{provider}/callback` routes already resolve any configured provider generically through `IEnumerable<IDnsPushProvider>` and `DnsPushProviderLookup.FindConfiguredAsync`; the three push-button click handlers (`DomainDetail.razor`'s `PushDmarcRecordAsync`/`PushTlsrptRecordAsync`, `DomainMtaStsPanel.razor`'s `PushDnsRecordsAsync`) already read `_domain.DnsProvider.ToProviderKey()`, which picks up `"google-cloud-dns"` automatically once Detection's `ToProviderKey()` arm exists; `Program.cs`'s zone-resolution mismatch guard (added by the earlier DNS zone resolution work) is provider-agnostic already. This is the interface abstraction doing exactly what it's for.

## Testing

- Neither `CloudflareDnsPushProvider` nor `AzureDnsPushProvider` has a dedicated unit test file today - this codebase has no existing infrastructure for mocking a live multi-step OAuth-plus-external-API push flow, confirmed by checking. `GoogleCloudDnsPushProvider` matches that existing convention rather than introducing coverage the other two don't have.
- `GoogleCloudDnsSettingsServiceTests.cs` mirrors `CloudflareDnsSettingsServiceTests.cs`/`AzureDnsSettingsServiceTests.cs` exactly - pure DB logic, genuinely testable, and already tested for the other two providers.
- New `DnsProviderDetector` test(s) for the `.googledomains.com` suffix, following the existing per-suffix test pattern.
- New `DnsProviderStatusPresentationTests`/`DetectedDnsProviderExtensions` test case(s) for the new `GoogleCloudDns` member.
- No bUnit component tests (confirmed established convention this session) - the new `DnsPushSettings.razor` section is verified by build plus manual check.

## Migration path

New migration adding the `GoogleCloudDnsSettings` table (columns: `Id`, `ClientId` nullable text, `ClientSecretConfigured` bool) plus its seed row (`Id = 1`, matching `AzureDnsSettings`/`CloudflareDnsSettings`'s existing seed pattern) - no data migration concern, this is a brand-new table with no prior data. `DetectedDnsProvider.GoogleCloudDns` needs no migration of its own, per Data model changes above.
