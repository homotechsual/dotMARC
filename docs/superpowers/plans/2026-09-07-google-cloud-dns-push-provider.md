# Google Cloud DNS Push Provider Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Google Cloud DNS as a third supported `IDnsPushProvider`, so a monitored domain hosted there gets the same one-click OAuth push experience Cloudflare and Azure DNS already have.

**Architecture:** A new `GoogleCloudDnsPushProvider : IDnsPushProvider` following the exact shape `CloudflareDnsPushProvider`/`AzureDnsPushProvider` already establish (DB-backed OAuth client credentials, a fresh Authorization Code + PKCE exchange per push, nothing about the token ever persisted) - the interface abstraction already in place means the push routes, the three push-button handlers, and the zone-resolution mismatch guard need zero changes. Two real differences from the existing two providers: zone discovery has to enumerate every GCP project the user can see (Google has no direct "find the zone matching this name" lookup the way Cloudflare/Azure do), and every record mutation goes through Cloud DNS's atomic `Change` resource instead of separate create/update/delete calls.

**Tech Stack:** .NET 10, Blazor Server, EF Core + Npgsql, MudBlazor 9.8.0, xUnit + Testcontainers (Postgres).

**Spec:** `docs/superpowers/specs/2026-09-07-google-cloud-dns-push-provider-design.md`

## Global Constraints

- `GoogleCloudDnsPushProvider`'s `ProviderKey` is exactly `"google-cloud-dns"` - matches the `{provider}` route segment convention (`"cloudflare"`, `"azure-dns"`) and `DetectedDnsProviderExtensions.ToProviderKey()`'s new arm.
- No admin-configured Project ID setting anywhere - zone discovery always enumerates every project the authorizing user's own token can see. This is deliberate: a single hard-coded project is exactly the mistake `AzureDnsSettings.TenantId` was (recently removed for breaking per-customer delegation).
- `GoogleCloudDnsSettings` is structurally identical to `CloudflareDnsSettings` (`Id`, `ClientId`, `ClientSecretConfigured`, secret in `ISecretStore`) - no extra fields.
- Nothing about the OAuth access token is ever persisted - it exists only as a local variable for the duration of one `ExchangeAndPushAsync` call, same contract as the other two providers.
- `ReplaceRecordAsync`'s failure path always returns `DnsPushOutcome.ProviderError`, never `ReplaceFailedAfterDelete` - Cloud DNS's `Change` resource applies atomically, so there is no window where the old record is gone and the new one hasn't landed; every failure here means nothing changed at all.
- No change to `CloudflareDnsPushProvider.cs` or `AzureDnsPushProvider.cs`.
- No admin UI project field, no DNSSEC configuration, no private/internal zone support - public zones only.

---

### Task 1: Data model - `GoogleCloudDnsSettings`, `DetectedDnsProvider.GoogleCloudDns`, migration

**Files:**
- Create: `src/DotMarc/Notifications/GoogleCloudDnsSettings.cs`
- Create: `src/DotMarc/Notifications/GoogleCloudDnsSettingsService.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs`
- Modify: `src/DotMarc/Data/DetectedDnsProvider.cs`
- Test: `test/DotMarc.Tests/Notifications/GoogleCloudDnsSettingsServiceTests.cs`
- Create: EF Core migration (generated, verified by hand)

**Interfaces:**
- Produces: `GoogleCloudDnsSettings { Id, ClientId, ClientSecretConfigured }` with `SecretStoreKey = "GoogleCloudDns.ClientSecret"`; `GoogleCloudDnsSettingsService.GetAsync(DotMarcDbContext, CancellationToken)`/`SaveAsync(DotMarcDbContext, ISecretStore, GoogleCloudDnsSettings, string?, CancellationToken)`; `DetectedDnsProvider.GoogleCloudDns` (new enum member, after `AzureDns`) - all consumed by Tasks 2-4.

- [ ] **Step 1: Add the new enum member**

In `src/DotMarc/Data/DetectedDnsProvider.cs`, change:
```csharp
public enum DetectedDnsProvider
{
    NotChecked,
    Unknown,
    Cloudflare,
    AzureDns
}
```
to:
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
No migration needed for this part - it's a new string value in the already-`HasConversion<string>()`-configured `Domain.DnsProvider` column.

- [ ] **Step 2: Create the settings entity**

Create `src/DotMarc/Notifications/GoogleCloudDnsSettings.cs`:
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

- [ ] **Step 3: Create the settings service**

Create `src/DotMarc/Notifications/GoogleCloudDnsSettingsService.cs`:
```csharp
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

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

- [ ] **Step 4: Register the DbSet and seed row**

In `src/DotMarc/Data/DotMarcDbContext.cs`, change:
```csharp
    public DbSet<CloudflareDnsSettings> CloudflareDnsSettings => Set<CloudflareDnsSettings>();
    public DbSet<AzureDnsSettings> AzureDnsSettings => Set<AzureDnsSettings>();
```
to:
```csharp
    public DbSet<CloudflareDnsSettings> CloudflareDnsSettings => Set<CloudflareDnsSettings>();
    public DbSet<AzureDnsSettings> AzureDnsSettings => Set<AzureDnsSettings>();
    public DbSet<GoogleCloudDnsSettings> GoogleCloudDnsSettings => Set<GoogleCloudDnsSettings>();
```
and change:
```csharp
        modelBuilder.Entity<CloudflareDnsSettings>().HasData(new CloudflareDnsSettings { Id = 1 });
        modelBuilder.Entity<AzureDnsSettings>().HasData(new AzureDnsSettings { Id = 1 });
    }
}
```
to:
```csharp
        modelBuilder.Entity<CloudflareDnsSettings>().HasData(new CloudflareDnsSettings { Id = 1 });
        modelBuilder.Entity<AzureDnsSettings>().HasData(new AzureDnsSettings { Id = 1 });
        modelBuilder.Entity<GoogleCloudDnsSettings>().HasData(new GoogleCloudDnsSettings { Id = 1 });
    }
}
```

- [ ] **Step 5: Write the failing tests**

Create `test/DotMarc.Tests/Notifications/GoogleCloudDnsSettingsServiceTests.cs`, mirroring `CloudflareDnsSettingsServiceTests.cs` exactly:
```csharp
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class GoogleCloudDnsSettingsServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public GoogleCloudDnsSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    private DatabaseSecretStore CreateSecretStore() =>
        new(new FakeDbContextFactory(_connectionString), DataProtectionProvider.Create("DotMarc.Tests.GoogleCloudDnsSettingsService"));

    [Fact]
    public async Task SaveAsync_UpdatesClientId_AndLeavesSecretUnconfigured_WhenNoneProvided()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();

        await GoogleCloudDnsSettingsService.SaveAsync(context, secretStore, new GoogleCloudDnsSettings { ClientId = "client-id" }, newClientSecret: null);

        await using var verify = CreateContext();
        var saved = await GoogleCloudDnsSettingsService.GetAsync(verify);
        Assert.Equal("client-id", saved.ClientId);
        Assert.False(saved.ClientSecretConfigured);
        Assert.Null(await secretStore.GetSecretAsync(GoogleCloudDnsSettings.SecretStoreKey));
    }

    [Fact]
    public async Task SaveAsync_StoresTheSecretAndMarksItConfigured_WhenProvided()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();

        await GoogleCloudDnsSettingsService.SaveAsync(context, secretStore, new GoogleCloudDnsSettings { ClientId = "client-id" }, newClientSecret: "the-real-secret");

        await using var verify = CreateContext();
        var saved = await GoogleCloudDnsSettingsService.GetAsync(verify);
        Assert.True(saved.ClientSecretConfigured);
        Assert.Equal("the-real-secret", await secretStore.GetSecretAsync(GoogleCloudDnsSettings.SecretStoreKey));
    }

    [Fact]
    public async Task SaveAsync_LeavesAnExistingSecretInPlace_WhenNotReplaced()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();
        await GoogleCloudDnsSettingsService.SaveAsync(context, secretStore, new GoogleCloudDnsSettings { ClientId = "client-id" }, newClientSecret: "first-secret");

        await using var secondContext = CreateContext();
        await GoogleCloudDnsSettingsService.SaveAsync(secondContext, secretStore, new GoogleCloudDnsSettings { ClientId = "changed" }, newClientSecret: null);

        Assert.Equal("first-secret", await secretStore.GetSecretAsync(GoogleCloudDnsSettings.SecretStoreKey));

        await using var verify = CreateContext();
        var verified = await GoogleCloudDnsSettingsService.GetAsync(verify);
        Assert.True(verified.ClientSecretConfigured);
        Assert.Equal("changed", verified.ClientId);
    }
}
```

- [ ] **Step 6: Run the tests to verify they fail to compile**

Run: `dotnet test --filter FullyQualifiedName~GoogleCloudDnsSettingsServiceTests`
Expected: FAILS TO COMPILE - `GoogleCloudDnsSettings`/`GoogleCloudDnsSettingsService` don't exist as DB-mapped types yet (Steps 2-4 create the types but not the migration).

- [ ] **Step 7: Generate the migration**

Run: `dotnet ef migrations add AddGoogleCloudDnsSettings --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj`

- [ ] **Step 8: Verify the generated migration by hand**

Open the generated `*_AddGoogleCloudDnsSettings.cs` file. It should `CreateTable` for `GoogleCloudDnsSettings` (columns: `Id` int identity primary key, `ClientId` nullable text, `ClientSecretConfigured` bool) and `InsertData` the seed row (`Id = 1`). This is a brand-new table with no prior data, so - unlike the historically-bug-prone enum-column-default-value migrations elsewhere in this codebase - there is no default-value pitfall to check here. Confirm `DotMarcDbContextModelSnapshot.cs` was updated to match (automatic, just confirm it happened).

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~GoogleCloudDnsSettingsServiceTests`
Expected: PASS (all 3 tests green)

- [ ] **Step 10: Run the full test suite**

Run: `dotnet test`
Expected: PASS, no failures.

- [ ] **Step 11: Commit**

```bash
git add src/DotMarc/Notifications/GoogleCloudDnsSettings.cs src/DotMarc/Notifications/GoogleCloudDnsSettingsService.cs src/DotMarc/Data/DotMarcDbContext.cs src/DotMarc/Data/DetectedDnsProvider.cs src/DotMarc/Migrations/ test/DotMarc.Tests/Notifications/GoogleCloudDnsSettingsServiceTests.cs
git commit -m "Add Google Cloud DNS settings data model"
```

---

### Task 2: Detection and presentation

**Files:**
- Modify: `src/DotMarc/DnsPush/DnsProviderDetector.cs`
- Modify: `src/DotMarc/Reporting/DnsProviderStatusPresentation.cs`
- Modify: `src/DotMarc/DnsPush/DetectedDnsProviderExtensions.cs`
- Test: `test/DotMarc.Tests/DnsPush/DnsProviderDetectorTests.cs`
- Test: `test/DotMarc.Tests/Reporting/DnsProviderStatusPresentationTests.cs`

**Interfaces:**
- Consumes: `DetectedDnsProvider.GoogleCloudDns` (Task 1).
- Produces: `DnsProviderDetector.DetectAsync` recognizes `.googledomains.com` NS suffixes as `GoogleCloudDns`; `DnsProviderStatusPresentation.GetColor/GetLabel(DetectedDnsProvider.GoogleCloudDns)`; `DetectedDnsProviderExtensions.ToProviderKey()` maps `GoogleCloudDns` to `"google-cloud-dns"` - consumed by Task 3 (`ProviderKey`) and Task 4 (UI/DI wiring needs no further change beyond this, per the Global Constraints note that the push routes/handlers are already provider-agnostic).

- [ ] **Step 1: Write the failing detection test**

Add to `test/DotMarc.Tests/DnsPush/DnsProviderDetectorTests.cs`, after the existing `DetectAsync_ReturnsAzureDns_ForEachAzureDnsSuffix` theory:
```csharp
    [Fact]
    public async Task DetectAsync_ReturnsGoogleCloudDns_ForTheGoogleDomainsNsSuffix()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":2,"data":"ns-cloud-a1.googledomains.com."},{"type":2,"data":"ns-cloud-a2.googledomains.com."}]}
            """;

        var result = await detector.DetectAsync("contoso.io", CancellationToken.None);

        Assert.Equal(DetectedDnsProvider.GoogleCloudDns, result.Provider);
        Assert.Equal("contoso.io", result.ZoneName);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~DetectAsync_ReturnsGoogleCloudDns_ForTheGoogleDomainsNsSuffix`
Expected: FAIL - the suffix isn't recognized yet, so this currently returns `Unknown`.

- [ ] **Step 3: Add the Google Cloud DNS suffix to `DnsProviderDetector`**

In `src/DotMarc/DnsPush/DnsProviderDetector.cs`, change:
```csharp
    private static readonly string[] CloudflareNsSuffixes = [".ns.cloudflare.com"];
    private static readonly string[] AzureDnsNsSuffixes =
        [".azure-dns.com", ".azure-dns.net", ".azure-dns.org", ".azure-dns.info"];
```
to:
```csharp
    private static readonly string[] CloudflareNsSuffixes = [".ns.cloudflare.com"];
    private static readonly string[] AzureDnsNsSuffixes =
        [".azure-dns.com", ".azure-dns.net", ".azure-dns.org", ".azure-dns.info"];
    // Also the NS suffix the legacy Google Domains registrar (now Squarespace-operated) used and
    // still uses for unmigrated zones - there is no way to tell "a real Cloud DNS zone this app
    // can push to" apart from "a legacy Google Domains zone with no GCP project behind it" using
    // NS records alone. A push against the latter just fails cleanly as ZoneNotFound once zone
    // discovery searches every accessible project and finds nothing, same as any other unmatched
    // zone - accepted, not fixable without a different kind of signal than NS suffix matching.
    private static readonly string[] GoogleCloudDnsNsSuffixes = [".googledomains.com"];
```
and change:
```csharp
                    if (AzureDnsNsSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                    {
                        return new DnsProviderDetectionResult(DetectedDnsProvider.AzureDns, candidate);
                    }
                }
```
to:
```csharp
                    if (AzureDnsNsSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                    {
                        return new DnsProviderDetectionResult(DetectedDnsProvider.AzureDns, candidate);
                    }
                    if (GoogleCloudDnsNsSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                    {
                        return new DnsProviderDetectionResult(DetectedDnsProvider.GoogleCloudDns, candidate);
                    }
                }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test --filter FullyQualifiedName~DnsProviderDetectorTests`
Expected: PASS (all tests green, including the new one)

- [ ] **Step 5: Write the failing presentation test**

In `test/DotMarc.Tests/Reporting/DnsProviderStatusPresentationTests.cs`, change:
```csharp
    [Theory]
    [InlineData(DetectedDnsProvider.Cloudflare, Color.Success, "Cloudflare")]
    [InlineData(DetectedDnsProvider.AzureDns, Color.Success, "Azure DNS")]
    [InlineData(DetectedDnsProvider.Unknown, Color.Warning, "Not recognized")]
    [InlineData(DetectedDnsProvider.NotChecked, Color.Default, "Not checked yet")]
    public void GetColorAndGetLabel_MapEveryProviderToItsExpectedPresentation(DetectedDnsProvider provider, Color expectedColor, string expectedLabel)
```
to:
```csharp
    [Theory]
    [InlineData(DetectedDnsProvider.Cloudflare, Color.Success, "Cloudflare")]
    [InlineData(DetectedDnsProvider.AzureDns, Color.Success, "Azure DNS")]
    [InlineData(DetectedDnsProvider.GoogleCloudDns, Color.Success, "Google Cloud DNS")]
    [InlineData(DetectedDnsProvider.Unknown, Color.Warning, "Not recognized")]
    [InlineData(DetectedDnsProvider.NotChecked, Color.Default, "Not checked yet")]
    public void GetColorAndGetLabel_MapEveryProviderToItsExpectedPresentation(DetectedDnsProvider provider, Color expectedColor, string expectedLabel)
```

- [ ] **Step 6: Run the test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~DnsProviderStatusPresentationTests`
Expected: FAIL - the new `GoogleCloudDns` case falls through to the `_ => Color.Default`/`"Not checked yet"` default arms, not `Color.Success`/`"Google Cloud DNS"`.

- [ ] **Step 7: Add the new arm to `DnsProviderStatusPresentation`**

In `src/DotMarc/Reporting/DnsProviderStatusPresentation.cs`, change:
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
to:
```csharp
    public static Color GetColor(DetectedDnsProvider provider) => provider switch
    {
        DetectedDnsProvider.Cloudflare or DetectedDnsProvider.AzureDns or DetectedDnsProvider.GoogleCloudDns => Color.Success,
        DetectedDnsProvider.Unknown => Color.Warning,
        _ => Color.Default
    };

    public static string GetLabel(DetectedDnsProvider provider) => provider switch
    {
        DetectedDnsProvider.Cloudflare => "Cloudflare",
        DetectedDnsProvider.AzureDns => "Azure DNS",
        DetectedDnsProvider.GoogleCloudDns => "Google Cloud DNS",
        DetectedDnsProvider.Unknown => "Not recognized",
        _ => "Not checked yet"
    };
```

- [ ] **Step 8: Run the test to verify it passes**

Run: `dotnet test --filter FullyQualifiedName~DnsProviderStatusPresentationTests`
Expected: PASS

- [ ] **Step 9: Add the new arm to `ToProviderKey()` (no dedicated test file exists for this one - it's covered indirectly via Task 3's provider, matching how the other two arms have no direct unit test either)**

In `src/DotMarc/DnsPush/DetectedDnsProviderExtensions.cs`, change:
```csharp
    public static string? ToProviderKey(this DetectedDnsProvider provider) => provider switch
    {
        DetectedDnsProvider.Cloudflare => "cloudflare",
        DetectedDnsProvider.AzureDns => "azure-dns",
        _ => null
    };
```
to:
```csharp
    public static string? ToProviderKey(this DetectedDnsProvider provider) => provider switch
    {
        DetectedDnsProvider.Cloudflare => "cloudflare",
        DetectedDnsProvider.AzureDns => "azure-dns",
        DetectedDnsProvider.GoogleCloudDns => "google-cloud-dns",
        _ => null
    };
```

- [ ] **Step 10: Build and run the full test suite**

Run: `dotnet build` then `dotnet test`
Expected: clean build, PASS, no failures.

- [ ] **Step 11: Commit**

```bash
git add src/DotMarc/DnsPush/DnsProviderDetector.cs src/DotMarc/Reporting/DnsProviderStatusPresentation.cs src/DotMarc/DnsPush/DetectedDnsProviderExtensions.cs test/DotMarc.Tests/DnsPush/DnsProviderDetectorTests.cs test/DotMarc.Tests/Reporting/DnsProviderStatusPresentationTests.cs
git commit -m "Detect Google Cloud DNS and map it to a provider key"
```

---

### Task 3: `GoogleCloudDnsPushProvider`

**Files:**
- Create: `src/DotMarc/DnsPush/GoogleCloudDnsPushProvider.cs`

**Interfaces:**
- Consumes: `IDnsPushProvider` (existing interface), `GoogleCloudDnsSettings`/`GoogleCloudDnsSettingsService` (Task 1), `PkceGenerator` (existing, unchanged), `DnsRecordChange`/`DnsRecordChangeKind`/`DnsPushResult`/`DnsPushOutcome` (existing, unchanged).
- Produces: `GoogleCloudDnsPushProvider` implementing `IDnsPushProvider` with `ProviderKey => "google-cloud-dns"` - consumed by Task 4's DI registration.

This class has no existing test infrastructure to extend from - confirmed neither `CloudflareDnsPushProvider` nor `AzureDnsPushProvider` has a dedicated unit test file today (this codebase has no mocking setup for a live multi-step OAuth-plus-external-API flow). This task matches that existing convention: no new test file, verified by a clean build.

- [ ] **Step 1: Create the provider**

Create `src/DotMarc/DnsPush/GoogleCloudDnsPushProvider.cs`:
```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.DnsPush;

/// <summary>Pushes a DNS record change to Google Cloud DNS, authenticated via a fresh OAuth 2.0
/// Authorization Code + PKCE exchange each time - same "nothing about the end-user's push-time
/// token is ever persisted" contract as CloudflareDnsPushProvider/AzureDnsPushProvider. Unlike
/// those two, Google Cloud DNS has no direct "find the zone matching this domain name" lookup -
/// zones live inside GCP Projects, so finding the right one means enumerating every project the
/// authorizing user can see (Cloud Resource Manager API) and checking each one's zones - see
/// FindZoneAsync. Every mutation goes through Cloud DNS's Change resource
/// (https://cloud.google.com/dns/docs/reference/v1/changes), which applies atomically - unlike
/// Cloudflare/Azure's separate delete-then-create for a Replace, there is no window where the old
/// record is gone and the new one hasn't landed yet, so ReplaceRecordAsync never needs
/// DnsPushOutcome.ReplaceFailedAfterDelete: a failed Change call means nothing changed, full
/// stop.</summary>
public sealed class GoogleCloudDnsPushProvider : IDnsPushProvider
{
    private const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string ResourceManagerBase = "https://cloudresourcemanager.googleapis.com/v1";
    private const string DnsApiBase = "https://dns.googleapis.com/dns/v1";

    private readonly IDbContextFactory<DotMarcDbContext> _dbFactory;
    private readonly ISecretStore _secretStore;
    private readonly HttpClient _http;

    public GoogleCloudDnsPushProvider(IDbContextFactory<DotMarcDbContext> dbFactory, ISecretStore secretStore, HttpClient http)
    {
        _dbFactory = dbFactory;
        _secretStore = secretStore;
        _http = http;
    }

    public string ProviderKey => "google-cloud-dns";

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrEmpty(settings.ClientId) && settings.ClientSecretConfigured;
    }

    public async Task<string> BuildAuthorizationUrlAsync(string state, string codeChallenge, string redirectUri, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var query = new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId!,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            // ndev.clouddns.readwrite for the actual record push; cloudplatformprojects.readonly
            // (the narrowest scope Resource Manager's projects.list accepts) for FindZoneAsync's
            // project enumeration below. No access_type/prompt=consent - this flow never requests
            // or needs a refresh token, same as Cloudflare/Azure.
            ["scope"] = "https://www.googleapis.com/auth/ndev.clouddns.readwrite https://www.googleapis.com/auth/cloudplatformprojects.readonly",
            // Without this, a browser with an active Google session silently reuses it - same
            // reasoning as AzureDnsPushProvider's prompt=select_account.
            ["prompt"] = "select_account",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };
        return AuthorizationEndpoint + "?" + string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    public async Task<DnsPushResult> ExchangeAndPushAsync(
        string code, string codeVerifier, string redirectUri, IReadOnlyList<DnsRecordChange> changes, CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var clientSecret = await _secretStore.GetSecretAsync(GoogleCloudDnsSettings.SecretStoreKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(settings.ClientId) || string.IsNullOrEmpty(clientSecret))
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, "Google Cloud DNS push is not configured for this deployment.");
        }

        var accessToken = await ExchangeCodeForTokenAsync(settings.ClientId, clientSecret, code, codeVerifier, redirectUri, cancellationToken).ConfigureAwait(false);
        if (accessToken is null)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, "Google rejected the authorization code exchange.");
        }

        foreach (var change in changes)
        {
            var result = await PushOneChangeAsync(change, accessToken, cancellationToken).ConfigureAwait(false);
            if (result.Outcome != DnsPushOutcome.Pushed)
            {
                return result;
            }
        }

        return new DnsPushResult(DnsPushOutcome.Pushed, null);
    }

    private async Task<DnsPushResult> PushOneChangeAsync(DnsRecordChange change, string accessToken, CancellationToken cancellationToken)
    {
        var (projectId, managedZoneName, errorStatus) = await FindZoneAsync(change.ZoneName, accessToken, cancellationToken).ConfigureAwait(false);
        if (errorStatus.HasValue)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Google rejected the project/zone lookup ({errorStatus}).");
        }
        if (projectId is null || managedZoneName is null)
        {
            return new DnsPushResult(DnsPushOutcome.ZoneNotFound, $"Couldn't find {change.ZoneName} in any Google Cloud project you authorized.");
        }

        return change.Kind switch
        {
            DnsRecordChangeKind.Merge => await UpdateExistingRecordAsync(projectId, managedZoneName, accessToken, change, cancellationToken).ConfigureAwait(false),
            DnsRecordChangeKind.Replace => await ReplaceRecordAsync(projectId, managedZoneName, accessToken, change, cancellationToken).ConfigureAwait(false),
            _ => await CreateRecordAsync(projectId, managedZoneName, accessToken, change, cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<GoogleCloudDnsSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await GoogleCloudDnsSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ExchangeCodeForTokenAsync(string clientId, string clientSecret, string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code_verifier"] = codeVerifier
            })
        };
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return token?.AccessToken;
    }

    /// <summary>Google Cloud DNS has no "find the zone matching this domain name" lookup the way
    /// Cloudflare's GET /zones?name=... or Azure's per-subscription zone enumeration does - zones
    /// live inside GCP Projects, so this enumerates every project the authorizing user can see
    /// (Resource Manager's projects.list, paginated) and checks each one's managed zones (Cloud
    /// DNS's managedZones.list, also paginated) for a dnsName match. First match across the whole
    /// search wins, same as AzureDnsPushProvider.FindZoneAsync's first-subscription-first-zone-wins
    /// behavior.</summary>
    private async Task<(string? ProjectId, string? ManagedZoneName, int? ErrorStatusCode)> FindZoneAsync(string zoneName, string accessToken, CancellationToken cancellationToken)
    {
        var targetDnsName = zoneName.TrimEnd('.') + ".";

        string? projectsPageToken = null;
        do
        {
            var projectsUrl = $"{ResourceManagerBase}/projects" + (projectsPageToken is null ? "" : $"?pageToken={Uri.EscapeDataString(projectsPageToken)}");
            using var projectsRequest = new HttpRequestMessage(HttpMethod.Get, projectsUrl);
            projectsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var projectsResponse = await _http.SendAsync(projectsRequest, cancellationToken).ConfigureAwait(false);
            if (!projectsResponse.IsSuccessStatusCode)
            {
                return (null, null, (int)projectsResponse.StatusCode);
            }
            var projectsPage = await projectsResponse.Content.ReadFromJsonAsync<ProjectsListResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var project in projectsPage?.Projects ?? [])
            {
                var managedZoneName = await FindManagedZoneAsync(project.ProjectId, targetDnsName, accessToken, cancellationToken).ConfigureAwait(false);
                if (managedZoneName is not null)
                {
                    return (project.ProjectId, managedZoneName, null);
                }
            }

            projectsPageToken = projectsPage?.NextPageToken;
        } while (!string.IsNullOrEmpty(projectsPageToken));

        return (null, null, null);
    }

    private async Task<string?> FindManagedZoneAsync(string projectId, string targetDnsName, string accessToken, CancellationToken cancellationToken)
    {
        string? zonesPageToken = null;
        do
        {
            var zonesUrl = $"{DnsApiBase}/projects/{projectId}/managedZones" + (zonesPageToken is null ? "" : $"?pageToken={Uri.EscapeDataString(zonesPageToken)}");
            using var zonesRequest = new HttpRequestMessage(HttpMethod.Get, zonesUrl);
            zonesRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var zonesResponse = await _http.SendAsync(zonesRequest, cancellationToken).ConfigureAwait(false);
            if (!zonesResponse.IsSuccessStatusCode)
            {
                // A project the caller can list but not query Cloud DNS in (API not enabled, no
                // dns.viewer role there, etc.) - skip it and keep searching other projects rather
                // than failing the whole search over one inaccessible project.
                return null;
            }
            var zonesPage = await zonesResponse.Content.ReadFromJsonAsync<ManagedZonesListResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);

            var match = zonesPage?.ManagedZones?.FirstOrDefault(z => string.Equals(z.DnsName, targetDnsName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match.Name;
            }

            zonesPageToken = zonesPage?.NextPageToken;
        } while (!string.IsNullOrEmpty(zonesPageToken));

        return null;
    }

    private async Task<DnsPushResult> CreateRecordAsync(string projectId, string managedZoneName, string accessToken, DnsRecordChange change, CancellationToken cancellationToken)
    {
        var fqdn = change.Name.TrimEnd('.') + ".";
        var existing = await GetExistingRrsetAsync(projectId, managedZoneName, fqdn, change.RecordType, accessToken, cancellationToken).ConfigureAwait(false);
        if (existing.ErrorStatusCode.HasValue)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Google rejected the record lookup ({existing.ErrorStatusCode}).");
        }
        if (existing.Rrset is not null)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"A DNS record already exists at {change.Name} - remove it or update it manually rather than risk overwriting it.");
        }

        var newRrset = new ResourceRecordSet(fqdn, change.RecordType, 3600, [BuildRrdata(change)]);
        return await ApplyChangeAsync(projectId, managedZoneName, accessToken, additions: [newRrset], deletions: [], cancellationToken).ConfigureAwait(false);
    }

    private async Task<DnsPushResult> UpdateExistingRecordAsync(string projectId, string managedZoneName, string accessToken, DnsRecordChange change, CancellationToken cancellationToken)
    {
        var fqdn = change.Name.TrimEnd('.') + ".";
        var existing = await GetExistingRrsetAsync(projectId, managedZoneName, fqdn, change.RecordType, accessToken, cancellationToken).ConfigureAwait(false);
        if (existing.ErrorStatusCode.HasValue)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Google rejected the record lookup ({existing.ErrorStatusCode}).");
        }
        if (existing.Rrset is null)
        {
            return new DnsPushResult(DnsPushOutcome.ZoneNotFound, $"{change.Name} no longer exists at Google Cloud DNS - it may have been removed since this page loaded.");
        }

        var newRrset = new ResourceRecordSet(fqdn, change.RecordType, 3600, [BuildRrdata(change)]);
        return await ApplyChangeAsync(projectId, managedZoneName, accessToken, additions: [newRrset], deletions: [existing.Rrset], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes whatever record of change.ExistingRecordType currently exists at
    /// change.Name and creates a change.RecordType record with change.DesiredValue in its place,
    /// in ONE atomic Cloud DNS Change call - unlike CloudflareDnsPushProvider/AzureDnsPushProvider's
    /// separate delete-then-create, there is no window where the deletion has landed but the
    /// creation hasn't, so a failure here always means nothing changed
    /// (DnsPushOutcome.ProviderError, never ReplaceFailedAfterDelete - that outcome describes a
    /// partial-failure state this provider's atomic Change API cannot produce).</summary>
    private async Task<DnsPushResult> ReplaceRecordAsync(string projectId, string managedZoneName, string accessToken, DnsRecordChange change, CancellationToken cancellationToken)
    {
        var existingType = change.ExistingRecordType ?? change.RecordType;
        var fqdn = change.Name.TrimEnd('.') + ".";

        var existing = await GetExistingRrsetAsync(projectId, managedZoneName, fqdn, existingType, accessToken, cancellationToken).ConfigureAwait(false);
        if (existing.ErrorStatusCode.HasValue)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Google rejected the record lookup ({existing.ErrorStatusCode}) - nothing was changed.");
        }
        if (existing.Rrset is null)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"The {existingType} record at {change.Name} no longer exists at Google Cloud DNS - it may have been removed since this page loaded. Nothing was changed; try again.");
        }

        var newRrset = new ResourceRecordSet(fqdn, change.RecordType, 3600, [BuildRrdata(change)]);
        var result = await ApplyChangeAsync(projectId, managedZoneName, accessToken, additions: [newRrset], deletions: [existing.Rrset], cancellationToken).ConfigureAwait(false);
        return result.Outcome == DnsPushOutcome.Pushed
            ? result
            : new DnsPushResult(DnsPushOutcome.ProviderError, $"{result.DetailMessage} Nothing was changed - Google applies this kind of change atomically, so a failed request never leaves {change.Name} without a record.");
    }

    private async Task<(ResourceRecordSet? Rrset, int? ErrorStatusCode)> GetExistingRrsetAsync(string projectId, string managedZoneName, string fqdn, string recordType, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{DnsApiBase}/projects/{projectId}/managedZones/{managedZoneName}/rrsets?name={Uri.EscapeDataString(fqdn)}&type={Uri.EscapeDataString(recordType)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (null, (int)response.StatusCode);
        }
        var page = await response.Content.ReadFromJsonAsync<RrsetsListResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return (page?.Rrsets?.FirstOrDefault(), null);
    }

    private async Task<DnsPushResult> ApplyChangeAsync(string projectId, string managedZoneName, string accessToken, List<ResourceRecordSet> additions, List<ResourceRecordSet> deletions, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{DnsApiBase}/projects/{projectId}/managedZones/{managedZoneName}/changes")
        {
            Content = JsonContent.Create(new ChangeRequest(additions, deletions))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? new DnsPushResult(DnsPushOutcome.Pushed, null)
            : new DnsPushResult(DnsPushOutcome.ProviderError, $"Google rejected the record push ({(int)response.StatusCode}).");
    }

    /// <summary>Cloud DNS's rrdatas entries are literal zone-file text, so a TXT value must be
    /// quoted (an unquoted value is non-conformant) - same reasoning as
    /// CloudflareDnsPushProvider.BuildContent. Every value this app pushes (DMARC/TLSRPT policy
    /// text, the MTA-STS asuid verification token) is plain text with no embedded quotes, so the
    /// escape only guards against a value that happens to contain one. CNAME (and any other
    /// non-TXT type) content is never zone-file text and must NOT be quoted.</summary>
    private static string BuildRrdata(DnsRecordChange change) =>
        string.Equals(change.RecordType, "TXT", StringComparison.OrdinalIgnoreCase)
            ? $"\"{change.DesiredValue.Replace("\"", "\\\"")}\""
            : change.DesiredValue;

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);
    private sealed record ProjectsListResponse(
        [property: JsonPropertyName("projects")] List<ProjectEntry>? Projects,
        [property: JsonPropertyName("nextPageToken")] string? NextPageToken);
    private sealed record ProjectEntry([property: JsonPropertyName("projectId")] string ProjectId);
    private sealed record ManagedZonesListResponse(
        [property: JsonPropertyName("managedZones")] List<ManagedZoneEntry>? ManagedZones,
        [property: JsonPropertyName("nextPageToken")] string? NextPageToken);
    private sealed record ManagedZoneEntry(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("dnsName")] string DnsName);
    private sealed record RrsetsListResponse([property: JsonPropertyName("rrsets")] List<ResourceRecordSet>? Rrsets);
    private sealed record ResourceRecordSet(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("ttl")] int Ttl,
        [property: JsonPropertyName("rrdatas")] List<string> Rrdatas);
    private sealed record ChangeRequest(
        [property: JsonPropertyName("additions")] List<ResourceRecordSet> Additions,
        [property: JsonPropertyName("deletions")] List<ResourceRecordSet> Deletions);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: builds cleanly, 0 warnings, 0 errors. `IDnsPushProvider` is not yet registered in DI (Task 4) and nothing calls `GoogleCloudDnsPushProvider` yet, so this compiles standalone.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test`
Expected: PASS, no failures (no new tests added by this task, per the note above - confirm nothing regressed).

- [ ] **Step 4: Commit**

```bash
git add src/DotMarc/DnsPush/GoogleCloudDnsPushProvider.cs
git commit -m "Add GoogleCloudDnsPushProvider"
```

---

### Task 4: UI and DI wiring

**Files:**
- Modify: `src/DotMarc/Components/Pages/DnsPushSettings.razor`
- Modify: `src/DotMarc/Program.cs`

**Interfaces:**
- Consumes: `GoogleCloudDnsSettings`/`GoogleCloudDnsSettingsService` (Task 1), `GoogleCloudDnsPushProvider` (Task 3).
- Produces: nothing later tasks depend on - this is the final task.

- [ ] **Step 1: Add the Google Cloud DNS section to the settings page**

In `src/DotMarc/Components/Pages/DnsPushSettings.razor`, change:
```razor
@if (_azureDnsSettings is not null)
{
    <MudPaper Class="pa-4">
        <MudText Typo="Typo.h5" Class="mb-4">Azure DNS</MudText>
        <MudGrid>
            <MudItem xs="12" md="6">
                <MudTextField Label="Client ID" @bind-Value="_azureDnsSettings.ClientId" Variant="Variant.Outlined" />
            </MudItem>
            <MudItem xs="12" md="6">
                <MudTextField Label="Client secret" @bind-Value="_newAzureDnsClientSecret" InputType="InputType.Password" Variant="Variant.Outlined"
                              HelperText="@(_azureDnsSettings.ClientSecretConfigured ? "A secret is already configured - leave blank to keep it." : "No secret configured yet.")" />
            </MudItem>
        </MudGrid>
        <MudButton Variant="Variant.Filled" Color="Color.Primary" Class="mt-4" OnClick="SaveAzureDnsAsync">Save Azure DNS settings</MudButton>
    </MudPaper>
}
```
to:
```razor
@if (_azureDnsSettings is not null)
{
    <MudPaper Class="pa-4 mb-4">
        <MudText Typo="Typo.h5" Class="mb-4">Azure DNS</MudText>
        <MudGrid>
            <MudItem xs="12" md="6">
                <MudTextField Label="Client ID" @bind-Value="_azureDnsSettings.ClientId" Variant="Variant.Outlined" />
            </MudItem>
            <MudItem xs="12" md="6">
                <MudTextField Label="Client secret" @bind-Value="_newAzureDnsClientSecret" InputType="InputType.Password" Variant="Variant.Outlined"
                              HelperText="@(_azureDnsSettings.ClientSecretConfigured ? "A secret is already configured - leave blank to keep it." : "No secret configured yet.")" />
            </MudItem>
        </MudGrid>
        <MudButton Variant="Variant.Filled" Color="Color.Primary" Class="mt-4" OnClick="SaveAzureDnsAsync">Save Azure DNS settings</MudButton>
    </MudPaper>
}

@if (_googleCloudDnsSettings is not null)
{
    <MudPaper Class="pa-4">
        <MudText Typo="Typo.h5" Class="mb-4">Google Cloud DNS</MudText>
        <MudGrid>
            <MudItem xs="12" md="6">
                <MudTextField Label="Client ID" @bind-Value="_googleCloudDnsSettings.ClientId" Variant="Variant.Outlined" />
            </MudItem>
            <MudItem xs="12" md="6">
                <MudTextField Label="Client secret" @bind-Value="_newGoogleCloudDnsClientSecret" InputType="InputType.Password" Variant="Variant.Outlined"
                              HelperText="@(_googleCloudDnsSettings.ClientSecretConfigured ? "A secret is already configured - leave blank to keep it." : "No secret configured yet.")" />
            </MudItem>
        </MudGrid>
        <MudButton Variant="Variant.Filled" Color="Color.Primary" Class="mt-4" OnClick="SaveGoogleCloudDnsAsync">Save Google Cloud DNS settings</MudButton>
    </MudPaper>
}
```
(Note the added `mb-4` on the Azure `MudPaper` - it previously had no bottom margin because it was the last section; it no longer is, now that Google Cloud DNS follows it.)

- [ ] **Step 2: Add the field, load, and save logic**

In `src/DotMarc/Components/Pages/DnsPushSettings.razor`'s `@code` block, change:
```csharp
    private CloudflareDnsSettings? _cloudflareSettings;
    private AzureDnsSettings? _azureDnsSettings;
    private string? _newCloudflareClientSecret;
    private string? _newAzureDnsClientSecret;

    protected override async Task OnInitializedAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        _cloudflareSettings = await CloudflareDnsSettingsService.GetAsync(db);
        _azureDnsSettings = await AzureDnsSettingsService.GetAsync(db);
    }
```
to:
```csharp
    private CloudflareDnsSettings? _cloudflareSettings;
    private AzureDnsSettings? _azureDnsSettings;
    private GoogleCloudDnsSettings? _googleCloudDnsSettings;
    private string? _newCloudflareClientSecret;
    private string? _newAzureDnsClientSecret;
    private string? _newGoogleCloudDnsClientSecret;

    protected override async Task OnInitializedAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        _cloudflareSettings = await CloudflareDnsSettingsService.GetAsync(db);
        _azureDnsSettings = await AzureDnsSettingsService.GetAsync(db);
        _googleCloudDnsSettings = await GoogleCloudDnsSettingsService.GetAsync(db);
    }
```

Then, immediately after the existing `SaveAzureDnsAsync` method, add:
```csharp
    private async Task SaveGoogleCloudDnsAsync()
    {
        if (_googleCloudDnsSettings is null)
        {
            return;
        }

        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            await GoogleCloudDnsSettingsService.SaveAsync(db, SecretStoreAccessor, _googleCloudDnsSettings, string.IsNullOrWhiteSpace(_newGoogleCloudDnsClientSecret) ? null : _newGoogleCloudDnsClientSecret);
            _newGoogleCloudDnsClientSecret = null;
            Snackbar.Add("Google Cloud DNS settings saved.", Severity.Success);

            await using var reloadDb = await DbFactory.CreateDbContextAsync();
            _googleCloudDnsSettings = await GoogleCloudDnsSettingsService.GetAsync(reloadDb);
        }
        catch (Exception)
        {
            Snackbar.Add("Failed to save Google Cloud DNS settings. Try again.", Severity.Error);
        }
    }
```

- [ ] **Step 3: Register the provider in DI**

In `src/DotMarc/Program.cs`, change:
```csharp
builder.Services.AddSingleton<DotMarc.DnsPush.AzureDnsPushProvider>();
builder.Services.AddSingleton<DotMarc.DnsPush.IDnsPushProvider>(sp => sp.GetRequiredService<DotMarc.DnsPush.AzureDnsPushProvider>());
```
to:
```csharp
builder.Services.AddSingleton<DotMarc.DnsPush.AzureDnsPushProvider>();
builder.Services.AddSingleton<DotMarc.DnsPush.IDnsPushProvider>(sp => sp.GetRequiredService<DotMarc.DnsPush.AzureDnsPushProvider>());

builder.Services.AddHttpClient<DotMarc.DnsPush.GoogleCloudDnsPushProvider>();
builder.Services.AddSingleton<DotMarc.DnsPush.IDnsPushProvider>(sp => sp.GetRequiredService<DotMarc.DnsPush.GoogleCloudDnsPushProvider>());
```
(`AddHttpClient<T>`, not `AddSingleton<T>` - `GoogleCloudDnsPushProvider`'s constructor takes an injected `HttpClient`, matching `CloudflareDnsPushProvider`'s registration shape, not `AzureDnsPushProvider`'s HttpClient-free one.)

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: builds cleanly, 0 warnings, 0 errors. This is the task where `GoogleCloudDnsPushProvider` first becomes reachable from the app - confirm no DI resolution errors by also starting the app if practical (see Step 6).

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS, no failures. No new tests in this task (UI + DI wiring only, no bUnit component tests in this codebase - confirmed established convention).

- [ ] **Step 6: Manually verify in the browser**

Start the app locally (`dotnet exec bin/Debug/net10.0/DotMarc.dll` from `src/DotMarc/`, per this project's Windows Defender workaround for freshly-built `.exe`s - or `dotnet run` if that's not an issue in the current environment). Navigate to `/dns-push/settings` and confirm: a third "Google Cloud DNS" section appears below Azure DNS, with Client ID and Client secret fields; saving it round-trips correctly (reload the page, confirm the Client ID persists and the secret's helper text switches to "A secret is already configured"). A full end-to-end push against a real Google Cloud DNS zone needs a real Google OAuth client and a real GCP project, which likely isn't available in this environment - note in the report whether that deeper check could be completed, and if not, that the settings-page round-trip plus a clean build are the verification this task could actually perform.

Expected: settings page shows and saves the new section correctly.

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Components/Pages/DnsPushSettings.razor src/DotMarc/Program.cs
git commit -m "Wire up Google Cloud DNS settings UI and DI registration"
```

---

## Final Verification

- [ ] Run the full test suite one more time: `dotnet test` - expect PASS, no failures.
- [ ] Run `dotnet build` - expect a clean build with no new warnings.
- [ ] Confirm the migration from Task 1 applies cleanly (already exercised by every Postgres-backed test class's `InitializeAsync`).
- [ ] Skim the spec's Non-goals once more: no change to `CloudflareDnsPushProvider.cs`/`AzureDnsPushProvider.cs` (confirmed - neither appears in any task's file list), no admin-configured project field, no DNSSEC, no private-zone support.
- [ ] Note for whoever deploys this: the Cloud DNS write scope is very likely a Google "sensitive" OAuth scope - production use beyond a small test-user allowlist will likely need the OAuth consent screen submitted for Google's review (see the spec's OAuth flow section). This is an operational follow-up, not something this plan's tasks can complete.
