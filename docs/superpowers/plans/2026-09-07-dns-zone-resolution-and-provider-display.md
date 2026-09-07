# DNS Zone Resolution & Provider Display Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix DNS auto-push and provider detection for a monitored domain that's a subdomain of a larger zone (e.g. `services.wrc.wales`, whose real zone is `wrc.wales`) by resolving the actual zone apex via an NS-record walk-up, and surface the detected provider/zone as a persisted, scheduled health check.

**Architecture:** `IDnsProviderDetector.DetectAsync` changes from returning a bare `DetectedDnsProvider` enum to a `DnsProviderDetectionResult(Provider, ZoneName)` record; `DnsProviderDetector`'s implementation walks from the given hostname up through its ancestor labels doing public NS lookups (same Cloudflare DNS-over-HTTPS call it already makes) until it finds the real zone cut. That single change threads through everywhere `ZoneName` was previously just the bare domain name: `Program.cs`'s push-callback endpoint (the actual bug fix - neither push provider's record-writing code needs to change, both already work correctly given the right zone name), a new persisted `Domain.DnsProvider`/`DnsZone` health check (`PollingService` cycle + UI), and four existing push-button click handlers that currently call `DetectAsync` live just to pick an OAuth provider.

**Tech Stack:** .NET 10, Blazor Server, EF Core + Npgsql, MudBlazor 9.8.0, xUnit + Testcontainers (Postgres).

**Spec:** `docs/superpowers/specs/2026-09-07-dns-zone-resolution-and-provider-display-design.md`

## Global Constraints

- `DetectedDnsProvider` moves from `src/DotMarc/DnsPush/DetectedDnsProvider.cs` to `src/DotMarc/Data/DetectedDnsProvider.cs`, gaining a new first member `NotChecked` (so the full enum is `{NotChecked, Unknown, Cloudflare, AzureDns}`) - it's now a `Domain` property, so it belongs alongside `MxCheckStatus`/`SpfCheckStatus`/etc.
- `IDnsProviderDetector.DetectAsync`'s return type changes from `Task<DetectedDnsProvider>` to `Task<DnsProviderDetectionResult>` where `DnsProviderDetectionResult(DetectedDnsProvider Provider, string ZoneName)` - every call site needs updating to read `.Provider`/`.ZoneName` instead of the bare enum.
- The NS-lookup walk is capped at exactly 6 total queries (the original hostname plus up to 5 ancestors) - a hard `for` loop bound, not an open-ended walk.
- An `HttpRequestException` on any single query in the walk aborts immediately with `(Unknown, <original input hostname>)` - it does NOT try further ancestors after a transport failure.
- Push-time zone resolution (`Program.cs`) is always a fresh live `DetectAsync` call, never a read of the persisted `Domain.DnsZone` - matching this codebase's existing convention that every push re-fetches live DNS state immediately before writing.
- Neither `AzureDnsPushProvider` nor `CloudflareDnsPushProvider` needs any code change - both already produce correct results given the right `ZoneName`, confirmed in the spec's investigation.
- No Public Suffix List, no new authenticated API calls for detection - the walk uses only the existing public Cloudflare DNS-over-HTTPS endpoint.
- This DOES need an EF Core migration (unlike the null-routed-domains feature that preceded it) - `Domain.DnsProvider`/`DnsZone`/`DnsProviderCheckedUtc` are brand new columns, not new members of an already-converted enum column.

---

### Task 1: Data model - move `DetectedDnsProvider`, add `Domain` fields, migration

**Files:**
- Delete: `src/DotMarc/DnsPush/DetectedDnsProvider.cs`
- Create: `src/DotMarc/Data/DetectedDnsProvider.cs`
- Modify: `src/DotMarc/DnsPush/IDnsProviderDetector.cs`
- Modify: `src/DotMarc/DnsPush/DnsProviderDetector.cs`
- Modify: `src/DotMarc/Data/Domain.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs`
- Modify: `test/DotMarc.Tests/Data/DotMarcDbContextTests.cs`
- Create: EF Core migration (generated, verified by hand)

**Interfaces:**
- Produces: `DotMarc.Data.DetectedDnsProvider { NotChecked, Unknown, Cloudflare, AzureDns }` (moved namespace, new first member) - every later task uses this exact name and location. `Domain.DnsProvider` (`DetectedDnsProvider`, default `NotChecked`), `Domain.DnsZone` (`string?`), `Domain.DnsProviderCheckedUtc` (`DateTimeOffset?`) - used by Task 4 (PollingService cycle) and Task 6 (UI).

- [ ] **Step 1: Delete the old enum file and create it in its new location**

Delete `src/DotMarc/DnsPush/DetectedDnsProvider.cs`.

Create `src/DotMarc/Data/DetectedDnsProvider.cs`:
```csharp
namespace DotMarc.Data;

/// <summary>The most recently detected DNS provider for a Domain - see
/// DotMarc.DnsPush.IDnsProviderDetector. NotChecked is listed first so it is the enum's (and the
/// database column's) default value; Unknown means the check ran but no known provider's NS suffix
/// matched at the resolved zone - still meaningfully different from never having checked at all.</summary>
public enum DetectedDnsProvider
{
    NotChecked,
    Unknown,
    Cloudflare,
    AzureDns
}
```

- [ ] **Step 2: Add `using DotMarc.Data;` to the two files that reference the moved type**

`DetectedDnsProvider` used to live in the same namespace (`DotMarc.DnsPush`) as `IDnsProviderDetector`/`DnsProviderDetector`, so neither file had any `using` for it. Now that it's moved, both need one. This step ONLY adds the using statement - do not change any other code in these two files (their actual logic rewrite is a later task).

In `src/DotMarc/DnsPush/IDnsProviderDetector.cs`, change:
```csharp
namespace DotMarc.DnsPush;

public interface IDnsProviderDetector
```
to:
```csharp
using DotMarc.Data;

namespace DotMarc.DnsPush;

public interface IDnsProviderDetector
```

In `src/DotMarc/DnsPush/DnsProviderDetector.cs`, change:
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.DnsPush;
```
to:
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using DotMarc.Data;

namespace DotMarc.DnsPush;
```

- [ ] **Step 3: Add the three new fields to `Domain`**

In `src/DotMarc/Data/Domain.cs`, change:
```csharp
    public List<string> MtaStsMxHosts { get; set; } = [];
    public int? HaloClientId { get; set; } // override; null means "use the Group's mapping"

    public List<Report> Reports { get; set; } = [];
```
to:
```csharp
    public List<string> MtaStsMxHosts { get; set; } = [];
    public int? HaloClientId { get; set; } // override; null means "use the Group's mapping"

    public DetectedDnsProvider DnsProvider { get; set; }
    public string? DnsZone { get; set; }
    public DateTimeOffset? DnsProviderCheckedUtc { get; set; }

    public List<Report> Reports { get; set; } = [];
```

- [ ] **Step 4: Register the new column's string conversion**

In `src/DotMarc/Data/DotMarcDbContext.cs`, change:
```csharp
            entity.Property(d => d.DkimCheckStatus).HasConversion<string>();
```
to:
```csharp
            entity.Property(d => d.DkimCheckStatus).HasConversion<string>();
            entity.Property(d => d.DnsProvider).HasConversion<string>();
```

- [ ] **Step 5: Write the default-value test**

Add to `test/DotMarc.Tests/Data/DotMarcDbContextTests.cs`, after `Domain_DmarcCheckStatus_DefaultsToNotChecked`:
```csharp
    [Fact]
    public void Domain_DnsProvider_DefaultsToNotChecked()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        context.SaveChanges();

        using var verify = CreateContext();
        var domain = verify.Domains.Single();
        Assert.Equal(DetectedDnsProvider.NotChecked, domain.DnsProvider);
        Assert.Null(domain.DnsZone);
        Assert.Null(domain.DnsProviderCheckedUtc);
    }
```

- [ ] **Step 6: Generate the migration**

Run: `dotnet ef migrations add AddDnsProviderDetection --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj`

- [ ] **Step 7: Verify the generated migration's default value by hand**

Open the generated `*_AddDnsProviderDetection.cs` file and find the `AddColumn` call for `DnsProvider`. It MUST read `defaultValue: "NotChecked"` (the enum's first member name as a string, matching `HasConversion<string>()`) - **not** `defaultValue: ""`. A prior feature in this codebase (the domain-health-checklist plan) shipped exactly this bug: EF's generator defaulted a new enum column to an empty string instead of the first member's name, which would throw on `Enum.Parse<DetectedDnsProvider>("")` for every pre-existing row the moment this column is read. If the generated file has `defaultValue: ""`, hand-edit it to `defaultValue: "NotChecked"` before proceeding. Also confirm `DotMarcDbContextModelSnapshot.cs` was updated to match (EF updates this automatically as part of `migrations add` - just confirm it's using `"NotChecked"` too, not `""`).

- [ ] **Step 8: Run the test to verify it fails, then passes**

Run: `dotnet test --filter FullyQualifiedName~DotMarcDbContextTests.Domain_DnsProvider_DefaultsToNotChecked`
Expected before the migration is applied to a fresh test database: the test framework runs `context.Database.MigrateAsync()` itself in `InitializeAsync`, so this test should just PASS once Steps 6-7 are done correctly. If it fails with a `PostgresException` about a missing column, the migration didn't generate correctly - re-check Step 6. If it fails with an `Enum.Parse` or similar exception, the default-value bug from Step 7 is still present - fix it.

- [ ] **Step 9: Run the full test suite**

Run: `dotnet test`
Expected: builds successfully. Note: this WILL show pre-existing compile or test failures in `DnsProviderDetectorTests.cs`, `Program.cs`, `DomainDetail.razor`, and `DomainMtaStsPanel.razor` at this point - those files still reference the OLD `Task<DetectedDnsProvider>` return type from `IDnsProviderDetector`, which Task 2 hasn't changed yet, so they still compile and pass as-is (this task didn't touch `DnsProviderDetector.DetectAsync`'s signature or body, only added a `using`). If anything outside those not-yet-touched files fails, investigate before committing.

- [ ] **Step 10: Commit**

```bash
git add src/DotMarc/Data/DetectedDnsProvider.cs src/DotMarc/DnsPush/IDnsProviderDetector.cs src/DotMarc/DnsPush/DnsProviderDetector.cs src/DotMarc/Data/Domain.cs src/DotMarc/Data/DotMarcDbContext.cs test/DotMarc.Tests/Data/DotMarcDbContextTests.cs src/DotMarc/Migrations/
git commit -m "Add persisted DNS provider/zone fields, move DetectedDnsProvider to Data"
```

---

### Task 2: Zone resolution - `DnsProviderDetectionResult` + walk-up rewrite

**Files:**
- Create: `src/DotMarc/DnsPush/DnsProviderDetectionResult.cs`
- Modify: `src/DotMarc/DnsPush/IDnsProviderDetector.cs`
- Modify: `src/DotMarc/DnsPush/DnsProviderDetector.cs`
- Test: `test/DotMarc.Tests/DnsPush/DnsProviderDetectorTests.cs`

**Interfaces:**
- Consumes: `DetectedDnsProvider` (Task 1, now in `DotMarc.Data`).
- Produces: `DnsProviderDetectionResult(DetectedDnsProvider Provider, string ZoneName)`; `IDnsProviderDetector.DetectAsync(string domainName, CancellationToken) -> Task<DnsProviderDetectionResult>` - consumed by Task 4 (PollingService), Task 5 (Program.cs push endpoint), Task 6 (UI's remaining live call for the DMARC-authorization mailbox domain).

- [ ] **Step 1: Create the result record**

Create `src/DotMarc/DnsPush/DnsProviderDetectionResult.cs`:
```csharp
namespace DotMarc.DnsPush;

/// <summary>The result of resolving a hostname to its real DNS zone and provider - see
/// DnsProviderDetector.DetectAsync. ZoneName is the hostname itself when it's already an apex, or
/// the nearest ancestor that actually has NS records otherwise. Provider is Unknown, and ZoneName is
/// the original input hostname unchanged, when no ancestor within the walk's 6-query cap has a
/// recognized (or any) NS delegation.</summary>
public sealed record DnsProviderDetectionResult(DotMarc.Data.DetectedDnsProvider Provider, string ZoneName);
```

- [ ] **Step 2: Change the interface's return type**

Replace the full contents of `src/DotMarc/DnsPush/IDnsProviderDetector.cs`:
```csharp
using DotMarc.Data;

namespace DotMarc.DnsPush;

public interface IDnsProviderDetector
{
    Task<DnsProviderDetectionResult> DetectAsync(string domainName, CancellationToken cancellationToken);
}
```

- [ ] **Step 3: Update the existing tests to the new return shape**

In `test/DotMarc.Tests/DnsPush/DnsProviderDetectorTests.cs`, every existing assertion of the form `Assert.Equal(DetectedDnsProvider.X, result)` becomes `Assert.Equal(DetectedDnsProvider.X, result.Provider)`. Apply this to all 5 assertions across `DetectAsync_ReturnsCloudflare_WhenNsRecordsAreCloudflares`, `DetectAsync_ReturnsAzureDns_ForEachAzureDnsSuffix`, `DetectAsync_ReturnsUnknown_ForAnUnrecognizedProvider`, `DetectAsync_ReturnsUnknown_WhenNoNsRecordsExist`, and `DetectAsync_ReturnsUnknown_WhenHttpRequestFails`. `DetectAsync_QueriesNsRecordType_ForTheGivenDomain` needs no assertion change (it only checks `handler.Requests[0]`, not the return value).

- [ ] **Step 4: Run the tests to verify they fail to compile**

Run: `dotnet test --filter FullyQualifiedName~DnsProviderDetectorTests`
Expected: FAILS TO COMPILE - `DnsProviderDetector.DetectAsync` still returns the old `Task<DetectedDnsProvider>` type, so `result.Provider` doesn't exist yet.

- [ ] **Step 5: Write the new walk-up tests**

Add to `test/DotMarc.Tests/DnsPush/DnsProviderDetectorTests.cs`, after `DetectAsync_ReturnsUnknown_WhenNoNsRecordsExist`:
```csharp
    [Fact]
    public async Task DetectAsync_WalksUpToTheParentZone_WhenTheSubdomainHasNoNsRecordsOfItsOwn()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBodies.Enqueue("""{"Status":0}"""); // services.wrc.wales: no Answer at all
        handler.ResponseBodies.Enqueue("""
            {"Status":0,"Answer":[{"type":2,"data":"ns1-03.azure-dns.com."},{"type":2,"data":"ns2-03.azure-dns.net."}]}
            """); // wrc.wales: Azure DNS

        var result = await detector.DetectAsync("services.wrc.wales", CancellationToken.None);

        Assert.Equal(DetectedDnsProvider.AzureDns, result.Provider);
        Assert.Equal("wrc.wales", result.ZoneName);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("services.wrc.wales", handler.Requests[0].RequestUri!.ToString());
        Assert.DoesNotContain("services.wrc.wales", handler.Requests[1].RequestUri!.ToString());
        Assert.Contains("name=wrc.wales", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task DetectAsync_WalksUpTwoLevels_WhenNeitherTheHostnameNorItsImmediateParentHasNsRecords()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBodies.Enqueue("""{"Status":0}"""); // a.b.contoso.io: no Answer
        handler.ResponseBodies.Enqueue("""{"Status":0}"""); // b.contoso.io: no Answer
        handler.ResponseBodies.Enqueue("""
            {"Status":0,"Answer":[{"type":2,"data":"ana.ns.cloudflare.com."}]}
            """); // contoso.io: Cloudflare

        var result = await detector.DetectAsync("a.b.contoso.io", CancellationToken.None);

        Assert.Equal(DetectedDnsProvider.Cloudflare, result.Provider);
        Assert.Equal("contoso.io", result.ZoneName);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task DetectAsync_ReturnsUnknownAndTheOriginalHostname_WhenNoAncestorWithinTheCapHasNsRecords()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBody = """{"Status":0}"""; // every query in the walk: no Answer

        var result = await detector.DetectAsync("f.e.d.c.b.a.contoso.io", CancellationToken.None);

        Assert.Equal(DetectedDnsProvider.Unknown, result.Provider);
        Assert.Equal("f.e.d.c.b.a.contoso.io", result.ZoneName);
        Assert.Equal(6, handler.Requests.Count);
    }

    [Fact]
    public async Task DetectAsync_StopsAtTheFirstAncestorWithNsRecords_EvenWhenTheProviderIsUnrecognized()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBodies.Enqueue("""{"Status":0}"""); // sub.contoso.io: no Answer
        handler.ResponseBodies.Enqueue("""
            {"Status":0,"Answer":[{"type":2,"data":"dns1.registrar-nameservers.com."}]}
            """); // contoso.io: NS records exist but match no known provider

        var result = await detector.DetectAsync("sub.contoso.io", CancellationToken.None);

        Assert.Equal(DetectedDnsProvider.Unknown, result.Provider);
        Assert.Equal("contoso.io", result.ZoneName);
        Assert.Equal(2, handler.Requests.Count);
    }
```

- [ ] **Step 6: Rewrite `DnsProviderDetector`**

Replace the full contents of `src/DotMarc/DnsPush/DnsProviderDetector.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using DotMarc.Data;

namespace DotMarc.DnsPush;

/// <summary>Detects whether a domain's DNS is hosted on Cloudflare or Azure DNS, and which zone
/// actually owns its records. Queries NS at the given hostname and, if that returns no NS records
/// (the hostname is a plain name inside a parent zone, not a delegated zone of its own - the normal
/// shape for a monitored subdomain like "services.wrc.wales" whose real zone is "wrc.wales"), walks
/// up one label at a time until an ancestor with real NS records is found. That ancestor IS the zone
/// apex - NS records only ever exist exactly at a zone cut, regardless of provider, so this needs no
/// authenticated API access to either provider just to find the answer. Capped at 6 total queries
/// (the original hostname plus up to 5 ancestors) to bound worst-case query count against a
/// pathological input; a real monitored domain resolves within 1-2 queries. Queries Cloudflare's own
/// DNS-over-HTTPS JSON API, same approach as DmarcDnsChecker/MtaStsDnsVerifier.</summary>
public sealed class DnsProviderDetector : IDnsProviderDetector
{
    private const int MaxQueries = 6;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] CloudflareNsSuffixes = [".ns.cloudflare.com"];
    private static readonly string[] AzureDnsNsSuffixes =
        [".azure-dns.com", ".azure-dns.net", ".azure-dns.org", ".azure-dns.info"];

    private readonly HttpClient _http;

    public DnsProviderDetector(HttpClient http) => _http = http;

    public async Task<DnsProviderDetectionResult> DetectAsync(string domainName, CancellationToken cancellationToken)
    {
        var candidate = domainName;
        for (var i = 0; i < MaxQueries; i++)
        {
            List<string> nsHosts;
            try
            {
                nsHosts = await QueryNsHostsAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                return new DnsProviderDetectionResult(DetectedDnsProvider.Unknown, domainName);
            }

            if (nsHosts.Count > 0)
            {
                foreach (var host in nsHosts)
                {
                    if (CloudflareNsSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                    {
                        return new DnsProviderDetectionResult(DetectedDnsProvider.Cloudflare, candidate);
                    }
                    if (AzureDnsNsSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                    {
                        return new DnsProviderDetectionResult(DetectedDnsProvider.AzureDns, candidate);
                    }
                }

                // NS records exist at this candidate but match no known provider - this IS the
                // zone (a real delegation, just to an unrecognized nameserver), so stop walking
                // rather than treating it as "not delegated yet" and searching further up.
                return new DnsProviderDetectionResult(DetectedDnsProvider.Unknown, candidate);
            }

            var nextDot = candidate.IndexOf('.');
            if (nextDot < 0)
            {
                break;
            }
            candidate = candidate[(nextDot + 1)..];
        }

        return new DnsProviderDetectionResult(DetectedDnsProvider.Unknown, domainName);
    }

    private async Task<List<string>> QueryNsHostsAsync(string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type=NS");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;

        return (parsed.Answer ?? []).Where(a => a.Type == 2).Select(a => a.Data.TrimEnd('.')).ToList();
    }

    private sealed record DnsOverHttpsResponse(
        [property: JsonPropertyName("Status")] int Status,
        [property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer(
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("data")] string Data);
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~DnsProviderDetectorTests`
Expected: PASS (all tests green, including the 4 new walk-up tests). Two pre-existing tests (`DetectAsync_ReturnsUnknown_WhenNoNsRecordsExist`, `DetectAsync_QueriesNsRecordType_ForTheGivenDomain`) now make 2 internal requests instead of 1, since their fixed `handler.ResponseBody` (unlike `ResponseBodies`, which drains) returns the same "no Answer" body for every request, so the walk proceeds one extra step to `"io"` before running out of dots. Neither test asserts a request count, so this doesn't break them - it's expected, not a bug.

- [ ] **Step 8: Run the full test suite**

Run: `dotnet test`
Expected: `DnsProviderDetectorTests` all pass. Compile errors remain in `Program.cs`, `DomainDetail.razor`, and `DomainMtaStsPanel.razor` (their `DnsProviderDetector.DetectAsync(...)` calls still expect the old bare-enum return type) - those are fixed in Tasks 5 and 6. If anything else fails, investigate before committing.

- [ ] **Step 9: Commit**

```bash
git add src/DotMarc/DnsPush/DnsProviderDetectionResult.cs src/DotMarc/DnsPush/IDnsProviderDetector.cs src/DotMarc/DnsPush/DnsProviderDetector.cs test/DotMarc.Tests/DnsPush/DnsProviderDetectorTests.cs
git commit -m "Resolve a domain's real DNS zone by walking up to the nearest delegated ancestor"
```

---

### Task 3: `DnsProviderStatusPresentation`

**Files:**
- Create: `src/DotMarc/Reporting/DnsProviderStatusPresentation.cs`
- Test: `test/DotMarc.Tests/Reporting/DnsProviderStatusPresentationTests.cs`

**Interfaces:**
- Consumes: `DetectedDnsProvider` (Task 1).
- Produces: `DnsProviderStatusPresentation.GetColor(DetectedDnsProvider) -> Color`, `GetLabel(DetectedDnsProvider) -> string` - consumed by Task 6 (UI).

- [ ] **Step 1: Write the failing test**

Create `test/DotMarc.Tests/Reporting/DnsProviderStatusPresentationTests.cs`:
```csharp
using DotMarc.Data;
using DotMarc.Reporting;
using MudBlazor;
using Xunit;

namespace DotMarc.Tests.Reporting;

public sealed class DnsProviderStatusPresentationTests
{
    [Theory]
    [InlineData(DetectedDnsProvider.Cloudflare, Color.Success, "Cloudflare")]
    [InlineData(DetectedDnsProvider.AzureDns, Color.Success, "Azure DNS")]
    [InlineData(DetectedDnsProvider.Unknown, Color.Warning, "Not recognized")]
    [InlineData(DetectedDnsProvider.NotChecked, Color.Default, "Not checked yet")]
    public void GetColorAndGetLabel_MapEveryProviderToItsExpectedPresentation(DetectedDnsProvider provider, Color expectedColor, string expectedLabel)
    {
        Assert.Equal(expectedColor, DnsProviderStatusPresentation.GetColor(provider));
        Assert.Equal(expectedLabel, DnsProviderStatusPresentation.GetLabel(provider));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails to compile**

Run: `dotnet test --filter FullyQualifiedName~DnsProviderStatusPresentationTests`
Expected: FAILS TO COMPILE - `DnsProviderStatusPresentation` doesn't exist yet.

- [ ] **Step 3: Implement `DnsProviderStatusPresentation`**

Create `src/DotMarc/Reporting/DnsProviderStatusPresentation.cs`:
```csharp
using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps DetectedDnsProvider to the MudBlazor color/label pair used on DomainDetail.razor's
/// Overview health checklist - same shared-presentation-logic precedent as DmarcStatusPresentation.
/// Unknown gets Color.Warning rather than the neutral default - it specifically means no auto-push
/// option is available, worth flagging the same way a missing record is.</summary>
public static class DnsProviderStatusPresentation
{
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
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test --filter FullyQualifiedName~DnsProviderStatusPresentationTests`
Expected: PASS

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: no new failures introduced by this task (pre-existing failures in `Program.cs`/`DomainDetail.razor`/`DomainMtaStsPanel.razor` from Task 2 remain until Tasks 5-6).

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Reporting/DnsProviderStatusPresentation.cs test/DotMarc.Tests/Reporting/DnsProviderStatusPresentationTests.cs
git commit -m "Add DNS provider status presentation"
```

---

### Task 4: `PollingService` scheduled check

**Files:**
- Modify: `src/DotMarc/Ingestion/PollingService.cs`
- Create: `test/DotMarc.Tests/Internal/FakeDnsProviderDetector.cs`
- Create: `test/DotMarc.Tests/Ingestion/DnsProviderCheckCycleTests.cs`

**Interfaces:**
- Consumes: `IDnsProviderDetector.DetectAsync` / `DnsProviderDetectionResult` (Task 2), `Domain.DnsProvider`/`DnsZone`/`DnsProviderCheckedUtc` (Task 1).
- Produces: `PollingService.DnsProviderCheckLeaderLockKey` (const long), `internal static Task RunSingleDnsProviderCheckAsync(Domain domain, IDnsProviderDetector detector, CancellationToken)`, `internal Task RunDnsProviderCheckCycleAsync(DotMarcDbContext context, IDnsProviderDetector detector, CancellationToken)` - consumed by Task 6 (UI's recheck button) and wired into `ExecuteAsync`.

- [ ] **Step 1: Create the fake test double**

Create `test/DotMarc.Tests/Internal/FakeDnsProviderDetector.cs`:
```csharp
using DotMarc.Data;
using DotMarc.DnsPush;

namespace DotMarc.Tests.Internal;

internal sealed class FakeDnsProviderDetector : IDnsProviderDetector
{
    public DnsProviderDetectionResult Result { get; set; } = new(DetectedDnsProvider.Unknown, "");
    public bool ShouldThrow { get; set; }
    public List<string> CheckedDomains { get; } = [];

    public Task<DnsProviderDetectionResult> DetectAsync(string domainName, CancellationToken cancellationToken)
    {
        CheckedDomains.Add(domainName);
        if (ShouldThrow)
        {
            throw new HttpRequestException("Simulated Cloudflare failure.");
        }
        return Task.FromResult(Result);
    }
}
```

- [ ] **Step 2: Write the failing cycle tests**

Create `test/DotMarc.Tests/Ingestion/DnsProviderCheckCycleTests.cs`:
```csharp
using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.Ingestion;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace DotMarc.Tests.Ingestion;

[Collection("Postgres")]
public sealed class DnsProviderCheckCycleTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DnsProviderCheckCycleTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static PollingService CreateService(DotMarcDbContext context) =>
        new(new FakeGraphMailboxClient(), context, NullLogger<PollingService>.Instance);

    [Fact]
    public async Task RunDnsProviderCheckCycleAsync_ChecksADomainNeverCheckedBefore()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "services.wrc.wales", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        var detector = new FakeDnsProviderDetector { Result = new(DetectedDnsProvider.AzureDns, "wrc.wales") };
        var service = CreateService(context);
        await service.RunDnsProviderCheckCycleAsync(context, detector, CancellationToken.None);

        Assert.Contains("services.wrc.wales", detector.CheckedDomains);
        var domain = context.Domains.Single();
        Assert.Equal(DetectedDnsProvider.AzureDns, domain.DnsProvider);
        Assert.Equal("wrc.wales", domain.DnsZone);
        Assert.NotNull(domain.DnsProviderCheckedUtc);
    }

    [Fact]
    public async Task RunDnsProviderCheckCycleAsync_SkipsADomainCheckedRecently()
    {
        using var context = CreateContext();
        var recentCheck = DateTimeOffset.UtcNow.AddHours(-1);
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            DnsProvider = DetectedDnsProvider.Cloudflare,
            DnsZone = "contoso.io",
            DnsProviderCheckedUtc = recentCheck
        });
        await context.SaveChangesAsync();

        var detector = new FakeDnsProviderDetector();
        var service = CreateService(context);
        await service.RunDnsProviderCheckCycleAsync(context, detector, CancellationToken.None);

        Assert.Empty(detector.CheckedDomains);
        Assert.Equal(recentCheck, context.Domains.Single().DnsProviderCheckedUtc);
    }

    [Fact]
    public async Task RunDnsProviderCheckCycleAsync_LeavesStatusUnchanged_WhenTheCheckItselfThrows()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            DnsProvider = DetectedDnsProvider.Unknown
        });
        await context.SaveChangesAsync();

        var detector = new FakeDnsProviderDetector { ShouldThrow = true };
        var service = CreateService(context);
        await service.RunDnsProviderCheckCycleAsync(context, detector, CancellationToken.None);

        using var verify = CreateContext();
        var verifyDomain = verify.Domains.Single();
        Assert.Equal(DetectedDnsProvider.Unknown, verifyDomain.DnsProvider);
        Assert.Null(verifyDomain.DnsProviderCheckedUtc);
    }

    [Fact]
    public async Task RunDnsProviderCheckCycleAsync_SkipsEntirely_WhenAnotherInstanceHoldsTheLock()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", PollingService.DnsProviderCheckLeaderLockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }

        var detector = new FakeDnsProviderDetector();
        var service = CreateService(context);
        await service.RunDnsProviderCheckCycleAsync(context, detector, CancellationToken.None);

        Assert.Empty(detector.CheckedDomains);

        await lockTransaction.RollbackAsync();
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail to compile**

Run: `dotnet test --filter FullyQualifiedName~DnsProviderCheckCycleTests`
Expected: FAILS TO COMPILE - `PollingService.DnsProviderCheckLeaderLockKey` and `RunDnsProviderCheckCycleAsync` don't exist yet.

- [ ] **Step 4: Add the lock key constant**

In `src/DotMarc/Ingestion/PollingService.cs`, change:
```csharp
    internal const long DkimCheckLeaderLockKey = 84_200_017;
```
to:
```csharp
    internal const long DkimCheckLeaderLockKey = 84_200_017;
    internal const long DnsProviderCheckLeaderLockKey = 84_200_019;
```

- [ ] **Step 5: Add `RunSingleDnsProviderCheckAsync`**

In `src/DotMarc/Ingestion/PollingService.cs`, immediately after the existing `RunSingleDkimCheckAsync` method, add:
```csharp
    /// <summary>DNS provider counterpart to RunSingleSpfCheckAsync - see its remarks. Unlike the
    /// other single-domain check methods, this one also writes a resolved zone name
    /// (Domain.DnsZone) alongside the status - see DnsProviderDetector's doc comment for what that
    /// means when the domain isn't itself the zone apex.</summary>
    internal static async Task RunSingleDnsProviderCheckAsync(Domain domain, IDnsProviderDetector dnsProviderDetector, CancellationToken cancellationToken)
    {
        var result = await dnsProviderDetector.DetectAsync(domain.Name, cancellationToken).ConfigureAwait(false);
        domain.DnsProvider = result.Provider;
        domain.DnsZone = result.ZoneName;
        domain.DnsProviderCheckedUtc = DateTimeOffset.UtcNow;
    }
```

- [ ] **Step 6: Add `RunDnsProviderCheckCycleAsync`**

In `src/DotMarc/Ingestion/PollingService.cs`, immediately after the `RunSingleDnsProviderCheckAsync` method just added, add:
```csharp
    /// <summary>Runs a DNS provider/zone check for every domain whose last check
    /// (DnsProviderCheckedUtc) is null or more than 24 hours old.</summary>
    internal async Task RunDnsProviderCheckCycleAsync(DotMarcDbContext context, IDnsProviderDetector dnsProviderDetector, CancellationToken cancellationToken)
    {
        var connectionString = context.Database.GetConnectionString()
            ?? throw new InvalidOperationException("DotMarcDbContext has no connection string configured.");

        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var lockTransaction = await lockConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        bool acquired;
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", DnsProviderCheckLeaderLockKey);
            acquired = (bool)(await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (!acquired)
        {
            _logger.LogDebug("Another instance already holds the DNS-provider-check lock for this cycle; skipping.");
            return;
        }

        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            var staleDomains = await context.Domains
                .Where(d => d.DnsProviderCheckedUtc == null || d.DnsProviderCheckedUtc < cutoff)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var anyUpdated = false;
            foreach (var domain in staleDomains)
            {
                try
                {
                    await RunSingleDnsProviderCheckAsync(domain, dnsProviderDetector, cancellationToken).ConfigureAwait(false);
                    anyUpdated = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DNS provider check failed for {Domain}; will retry next cycle.", domain.Name);
                }
            }

            if (anyUpdated)
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await lockTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
```

- [ ] **Step 7: Wire the cycle into `ExecuteAsync`**

In `src/DotMarc/Ingestion/PollingService.cs`, immediately after the existing DKIM check cycle block (the `try`/`catch` that calls `RunDkimCheckCycleAsync`, logging `"DKIM check cycle failed; will retry next interval."`), add:
```csharp
                    try
                    {
                        context.ChangeTracker.Clear();
                        var dnsProviderDetector = scope.ServiceProvider.GetRequiredService<IDnsProviderDetector>();
                        await RunDnsProviderCheckCycleAsync(context, dnsProviderDetector, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "DNS provider check cycle failed; will retry next interval.");
                    }
```
This needs `using DotMarc.DnsPush;` at the top of `PollingService.cs` for `IDnsProviderDetector` - check whether it's already present; if not, add it alongside the file's other `using` directives.

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~DnsProviderCheckCycleTests`
Expected: PASS (all 4 tests green)

- [ ] **Step 9: Run the full test suite**

Run: `dotnet test`
Expected: no new failures from this task. Compile errors remain in `Program.cs`, `DomainDetail.razor`, `DomainMtaStsPanel.razor` until Tasks 5-6.

- [ ] **Step 10: Commit**

```bash
git add src/DotMarc/Ingestion/PollingService.cs test/DotMarc.Tests/Internal/FakeDnsProviderDetector.cs test/DotMarc.Tests/Ingestion/DnsProviderCheckCycleTests.cs
git commit -m "Add a scheduled DNS provider/zone check"
```

---

### Task 5: Fix the actual bug - `Program.cs` push-callback zone resolution

**Files:**
- Modify: `src/DotMarc/Program.cs`

**Interfaces:**
- Consumes: `IDnsProviderDetector.DetectAsync` / `DnsProviderDetectionResult` (Task 2).
- Produces: nothing new for later tasks - this is the actual fix for the reported bug.

- [ ] **Step 1: Add `IDnsProviderDetector` to the callback endpoint's parameters**

In `src/DotMarc/Program.cs`, change the `/dns-push/{provider}/callback` handler's parameter list from:
```csharp
app.MapGet("/dns-push/{provider}/callback", async (
    string provider, string? code, string? state, string? error, HttpContext httpContext,
    IEnumerable<IDnsPushProvider> pushProviders, DnsPushStateProtector stateProtector,
    IDbContextFactory<DotMarcDbContext> dbContextFactory, IDmarcTxtLookup dmarcTxtLookup, ITlsrptTxtLookup tlsrptTxtLookup,
    IDmarcAuthorizationTxtLookup dmarcAuthorizationTxtLookup,
    IOptions<DotMarc.MtaSts.MtaStsOptions> mtaStsOptions, IOptions<GraphOptions> graphOptions,
    DotMarc.MtaSts.IMtaStsHostProvisioner mtaStsHostProvisioner, DotMarc.MtaSts.IMtaStsCnameLookup mtaStsCnameLookup,
    IAuthorizationService authorizationService, ILogger<Program> logger) =>
```
to:
```csharp
app.MapGet("/dns-push/{provider}/callback", async (
    string provider, string? code, string? state, string? error, HttpContext httpContext,
    IEnumerable<IDnsPushProvider> pushProviders, DnsPushStateProtector stateProtector,
    IDbContextFactory<DotMarcDbContext> dbContextFactory, IDmarcTxtLookup dmarcTxtLookup, ITlsrptTxtLookup tlsrptTxtLookup,
    IDmarcAuthorizationTxtLookup dmarcAuthorizationTxtLookup, IDnsProviderDetector dnsProviderDetector,
    IOptions<DotMarc.MtaSts.MtaStsOptions> mtaStsOptions, IOptions<GraphOptions> graphOptions,
    DotMarc.MtaSts.IMtaStsHostProvisioner mtaStsHostProvisioner, DotMarc.MtaSts.IMtaStsCnameLookup mtaStsCnameLookup,
    IAuthorizationService authorizationService, ILogger<Program> logger) =>
```

- [ ] **Step 2: Resolve `domain.Name`'s real zone once, with a provider-mismatch guard, for the three targets that use it**

Change:
```csharp
    if (error is not null || code is null)
    {
        return DnsPushPopupResult.Close("cancelled");
    }

    List<DnsRecordChange> changes;
    if (decodedState.PushTarget == "mta-sts")
```
to:
```csharp
    if (error is not null || code is null)
    {
        return DnsPushPopupResult.Close("cancelled");
    }

    // Every push target except dmarc-auth writes into domain.Name's own zone; dmarc-auth writes
    // into the mailbox's domain's zone instead and resolves that separately below. Resolving here
    // (rather than trusting whatever the UI's cached provider chip showed when the page loaded)
    // catches the case where DNS moved provider between page load and the user clicking push -
    // treated the same as ZoneNotFound rather than silently pushing to the wrong provider.
    string domainZone = domain.Name;
    if (decodedState.PushTarget != "dmarc-auth")
    {
        var domainZoneDetection = await dnsProviderDetector.DetectAsync(domain.Name, CancellationToken.None);
        var domainProviderKey = domainZoneDetection.Provider switch
        {
            DetectedDnsProvider.Cloudflare => "cloudflare",
            DetectedDnsProvider.AzureDns => "azure-dns",
            _ => null
        };
        if (!string.Equals(domainProviderKey, provider, StringComparison.OrdinalIgnoreCase))
        {
            return DnsPushPopupResult.Close("zone-not-found");
        }
        domainZone = domainZoneDetection.ZoneName;
    }

    List<DnsRecordChange> changes;
    if (decodedState.PushTarget == "mta-sts")
```

- [ ] **Step 3: Use `domainZone` instead of `domain.Name` in the `mta-sts`, `dmarc`, and `tlsrpt` branches**

In the `mta-sts` branch, change:
```csharp
        var cnameChange = existingCname is null
            ? new DnsRecordChange(DnsRecordChangeKind.Create, "CNAME", $"mta-sts.{domain.Name}", hostingHostname, null, domain.Name)
            : new DnsRecordChange(DnsRecordChangeKind.Merge, "CNAME", $"mta-sts.{domain.Name}", hostingHostname, existingCname, domain.Name);
```
to:
```csharp
        var cnameChange = existingCname is null
            ? new DnsRecordChange(DnsRecordChangeKind.Create, "CNAME", $"mta-sts.{domain.Name}", hostingHostname, null, domainZone)
            : new DnsRecordChange(DnsRecordChangeKind.Merge, "CNAME", $"mta-sts.{domain.Name}", hostingHostname, existingCname, domainZone);
```
and, further down in the same branch, change:
```csharp
                var asuidChange = existingAsuid is null
                    ? new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"asuid.mta-sts.{domain.Name}", verificationId, null, domain.Name)
                    : new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"asuid.mta-sts.{domain.Name}", verificationId, existingAsuid, domain.Name);
```
to:
```csharp
                var asuidChange = existingAsuid is null
                    ? new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"asuid.mta-sts.{domain.Name}", verificationId, null, domainZone)
                    : new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"asuid.mta-sts.{domain.Name}", verificationId, existingAsuid, domainZone);
```

In the `dmarc` branch, change all three:
```csharp
            changes = [new DnsRecordChange(DnsRecordChangeKind.Replace, "TXT", $"_dmarc.{domain.Name}", $"v=DMARC1; p=none; rua=mailto:{mailbox}", existing.DelegatedToCname, domain.Name, ExistingRecordType: "CNAME")];
        }
        else if (existing.DirectValue is null)
        {
            changes = [new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"_dmarc.{domain.Name}", $"v=DMARC1; p=none; rua=mailto:{mailbox}", null, domain.Name)];
        }
        else
        {
            var merged = DmarcRuaMerge.TryMerge(existing.DirectValue, mailbox);
            if (merged is null)
            {
                return DnsPushPopupResult.Close("unmergeable");
            }
            changes = [new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"_dmarc.{domain.Name}", merged, existing.DirectValue, domain.Name)];
```
to:
```csharp
            changes = [new DnsRecordChange(DnsRecordChangeKind.Replace, "TXT", $"_dmarc.{domain.Name}", $"v=DMARC1; p=none; rua=mailto:{mailbox}", existing.DelegatedToCname, domainZone, ExistingRecordType: "CNAME")];
        }
        else if (existing.DirectValue is null)
        {
            changes = [new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"_dmarc.{domain.Name}", $"v=DMARC1; p=none; rua=mailto:{mailbox}", null, domainZone)];
        }
        else
        {
            var merged = DmarcRuaMerge.TryMerge(existing.DirectValue, mailbox);
            if (merged is null)
            {
                return DnsPushPopupResult.Close("unmergeable");
            }
            changes = [new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"_dmarc.{domain.Name}", merged, existing.DirectValue, domainZone)];
```

In the final (`tlsrpt`) `else` branch, change all three:
```csharp
            changes = [new DnsRecordChange(DnsRecordChangeKind.Replace, "TXT", $"_smtp._tls.{domain.Name}", $"v=TLSRPTv1; rua=mailto:{mailbox}", existing.DelegatedToCname, domain.Name, ExistingRecordType: "CNAME")];
        }
        else if (existing.DirectValue is null)
        {
            changes = [new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"_smtp._tls.{domain.Name}", $"v=TLSRPTv1; rua=mailto:{mailbox}", null, domain.Name)];
        }
        else
        {
            var merged = TlsrptRuaMerge.TryMerge(existing.DirectValue, mailbox);
            if (merged is null)
            {
                return DnsPushPopupResult.Close("unmergeable");
            }
            changes = [new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"_smtp._tls.{domain.Name}", merged, existing.DirectValue, domain.Name)];
```
to:
```csharp
            changes = [new DnsRecordChange(DnsRecordChangeKind.Replace, "TXT", $"_smtp._tls.{domain.Name}", $"v=TLSRPTv1; rua=mailto:{mailbox}", existing.DelegatedToCname, domainZone, ExistingRecordType: "CNAME")];
        }
        else if (existing.DirectValue is null)
        {
            changes = [new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"_smtp._tls.{domain.Name}", $"v=TLSRPTv1; rua=mailto:{mailbox}", null, domainZone)];
        }
        else
        {
            var merged = TlsrptRuaMerge.TryMerge(existing.DirectValue, mailbox);
            if (merged is null)
            {
                return DnsPushPopupResult.Close("unmergeable");
            }
            changes = [new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"_smtp._tls.{domain.Name}", merged, existing.DirectValue, domainZone)];
```

- [ ] **Step 4: Resolve the mailbox domain's real zone, with the same mismatch guard, in the `dmarc-auth` branch**

Change:
```csharp
        var mailbox = graphOptions.Value.MailboxAddress;
        var mailboxDomain = mailbox[(mailbox.IndexOf('@') + 1)..];
        var authorizationName = $"{domain.Name}._report._dmarc.{mailboxDomain}";
        const string proposed = "v=DMARC1;";

        var existing = await dmarcAuthorizationTxtLookup.LookupAsync(authorizationName, CancellationToken.None);
        if (existing.DelegatedToCname is not null)
        {
            changes = [new DnsRecordChange(DnsRecordChangeKind.Replace, "TXT", authorizationName, proposed, existing.DelegatedToCname, mailboxDomain, ExistingRecordType: "CNAME")];
        }
        else if (existing.DirectValue is null)
        {
            changes = [new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", authorizationName, proposed, null, mailboxDomain)];
        }
        else
        {
            // No structured tags to preserve here (unlike DMARC/TLSRPT's rua= merge) - an
            // authorization record's only job is to exist with v=DMARC1, so an unexpected
            // existing value is simply overwritten once the confirm dialog (shown for any
            // existing-differs-from-proposed case, per DnsRecordPushDecision.NeedsConfirmation)
            // has been accepted.
            changes = [new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", authorizationName, proposed, existing.DirectValue, mailboxDomain)];
        }
```
to:
```csharp
        var mailbox = graphOptions.Value.MailboxAddress;
        var mailboxDomain = mailbox[(mailbox.IndexOf('@') + 1)..];
        var authorizationName = $"{domain.Name}._report._dmarc.{mailboxDomain}";
        const string proposed = "v=DMARC1;";

        var mailboxZoneDetection = await dnsProviderDetector.DetectAsync(mailboxDomain, CancellationToken.None);
        var mailboxProviderKey = mailboxZoneDetection.Provider switch
        {
            DetectedDnsProvider.Cloudflare => "cloudflare",
            DetectedDnsProvider.AzureDns => "azure-dns",
            _ => null
        };
        if (!string.Equals(mailboxProviderKey, provider, StringComparison.OrdinalIgnoreCase))
        {
            return DnsPushPopupResult.Close("zone-not-found");
        }
        var mailboxZone = mailboxZoneDetection.ZoneName;

        var existing = await dmarcAuthorizationTxtLookup.LookupAsync(authorizationName, CancellationToken.None);
        if (existing.DelegatedToCname is not null)
        {
            changes = [new DnsRecordChange(DnsRecordChangeKind.Replace, "TXT", authorizationName, proposed, existing.DelegatedToCname, mailboxZone, ExistingRecordType: "CNAME")];
        }
        else if (existing.DirectValue is null)
        {
            changes = [new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", authorizationName, proposed, null, mailboxZone)];
        }
        else
        {
            // No structured tags to preserve here (unlike DMARC/TLSRPT's rua= merge) - an
            // authorization record's only job is to exist with v=DMARC1, so an unexpected
            // existing value is simply overwritten once the confirm dialog (shown for any
            // existing-differs-from-proposed case, per DnsRecordPushDecision.NeedsConfirmation)
            // has been accepted.
            changes = [new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", authorizationName, proposed, existing.DirectValue, mailboxZone)];
        }
```

- [ ] **Step 5: Build**

Run: `dotnet build`
Expected: builds cleanly. `using DotMarc.Data;` (for `DetectedDnsProvider`) and `using DotMarc.DnsPush;` (for `IDnsProviderDetector`) are already present at the top of `Program.cs` - confirm both are still there; no new `using` needed.

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test`
Expected: no new failures from this task (`Program.cs`'s push endpoint has no existing automated test coverage, confirmed - so nothing here to regress). Compile errors remain in `DomainDetail.razor`/`DomainMtaStsPanel.razor` until Task 6.

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Program.cs
git commit -m "Resolve the real DNS zone before pushing, fixing subdomain-monitored domains"
```

---

### Task 6: UI - health-checklist row, header badge, and updating push handlers to use the persisted provider

**Files:**
- Modify: `src/DotMarc/Components/Pages/DomainDetail.razor`
- Modify: `src/DotMarc/Components/Shared/DomainMtaStsPanel.razor`

**Interfaces:**
- Consumes: `Domain.DnsProvider`/`DnsZone`/`DnsProviderCheckedUtc` (Task 1), `PollingService.RunSingleDnsProviderCheckAsync` (Task 4), `DnsProviderStatusPresentation` (Task 3), `IDnsProviderDetector.DetectAsync` / `DnsProviderDetectionResult` (Task 2).
- Produces: nothing later tasks depend on - this is the final task.

- [ ] **Step 1: Add the header badge**

In `src/DotMarc/Components/Pages/DomainDetail.razor`, change:
```razor
<PageTitle>dotMARC - @DomainName</PageTitle>
<MudButton Href="/dashboard" StartIcon="@Icons.Material.Filled.ArrowBack" Class="mb-2">Back</MudButton>
<MudText Typo="Typo.h4" Class="mb-4">@DomainName</MudText>
```
to:
```razor
<PageTitle>dotMARC - @DomainName</PageTitle>
<MudButton Href="/dashboard" StartIcon="@Icons.Material.Filled.ArrowBack" Class="mb-2">Back</MudButton>
<MudText Typo="Typo.h4" Class="mb-4">@DomainName</MudText>
@if (_domain is not null && _domain.DnsProvider is DetectedDnsProvider.Cloudflare or DetectedDnsProvider.AzureDns)
{
    <MudText Typo="Typo.body2" Class="mb-4 mud-text-secondary">
        DNS: @DnsProviderStatusPresentation.GetLabel(_domain.DnsProvider) (@_domain.DnsZone)
    </MudText>
}
```

- [ ] **Step 2: Add the "DNS provider" health-checklist row**

In `src/DotMarc/Components/Pages/DomainDetail.razor`, change:
```razor
                <DomainHealthCheckRow Title="DKIM"
                                      StatusColor="@DkimStatusPresentation.GetColor(_domain.DkimCheckStatus)"
                                      StatusLabel="@DkimStatusPresentation.GetLabel(_domain.DkimCheckStatus)"
                                      CheckedUtc="_domain.DkimCheckedUtc"
                                      Detail="@_domain.DkimCheckDetail">
                    <ActionContent>
                        <AuthorizeView Policy="DomainsEdit" Context="dkimAuthState">
                            <Authorized>
                                @if (_isRecheckingDkim)
                                {
                                    <MudProgressCircular Size="Size.Small" Indeterminate="true" />
                                }
                                else if (_domain.DkimSelectors.Count > 0)
                                {
                                    <MudIconButton Icon="@Icons.Material.Filled.Refresh" Size="Size.Small" OnClick="RecheckDkimAsync" title="Recheck now" />
                                }
                                <MudButton Variant="Variant.Text" Color="Color.Primary" OnClick="OpenDkimSelectorsDialogAsync">Configure selectors</MudButton>
                            </Authorized>
                        </AuthorizeView>
                    </ActionContent>
                </DomainHealthCheckRow>
            </MudPaper>
        </MudTabPanel>
```
to:
```razor
                <DomainHealthCheckRow Title="DKIM"
                                      StatusColor="@DkimStatusPresentation.GetColor(_domain.DkimCheckStatus)"
                                      StatusLabel="@DkimStatusPresentation.GetLabel(_domain.DkimCheckStatus)"
                                      CheckedUtc="_domain.DkimCheckedUtc"
                                      Detail="@_domain.DkimCheckDetail">
                    <ActionContent>
                        <AuthorizeView Policy="DomainsEdit" Context="dkimAuthState">
                            <Authorized>
                                @if (_isRecheckingDkim)
                                {
                                    <MudProgressCircular Size="Size.Small" Indeterminate="true" />
                                }
                                else if (_domain.DkimSelectors.Count > 0)
                                {
                                    <MudIconButton Icon="@Icons.Material.Filled.Refresh" Size="Size.Small" OnClick="RecheckDkimAsync" title="Recheck now" />
                                }
                                <MudButton Variant="Variant.Text" Color="Color.Primary" OnClick="OpenDkimSelectorsDialogAsync">Configure selectors</MudButton>
                            </Authorized>
                        </AuthorizeView>
                    </ActionContent>
                </DomainHealthCheckRow>

                <DomainHealthCheckRow Title="DNS provider"
                                      StatusColor="@DnsProviderStatusPresentation.GetColor(_domain.DnsProvider)"
                                      StatusLabel="@DnsProviderStatusPresentation.GetLabel(_domain.DnsProvider)"
                                      CheckedUtc="_domain.DnsProviderCheckedUtc"
                                      Detail="@(_domain.DnsZone is { } zone ? $"Zone: {zone}" : null)">
                    <ActionContent>
                        <AuthorizeView Policy="DomainsEdit" Context="dnsProviderAuthState">
                            <Authorized>
                                @if (_isRecheckingDnsProvider)
                                {
                                    <MudProgressCircular Size="Size.Small" Indeterminate="true" />
                                }
                                else
                                {
                                    <MudIconButton Icon="@Icons.Material.Filled.Refresh" Size="Size.Small" OnClick="RecheckDnsProviderAsync" title="Recheck now" />
                                }
                            </Authorized>
                        </AuthorizeView>
                    </ActionContent>
                </DomainHealthCheckRow>
            </MudPaper>
        </MudTabPanel>
```

- [ ] **Step 3: Add the `_isRecheckingDnsProvider` field**

In `src/DotMarc/Components/Pages/DomainDetail.razor`, change:
```csharp
    private bool _isRecheckingDkim;
```
to:
```csharp
    private bool _isRecheckingDkim;
    private bool _isRecheckingDnsProvider;
```

- [ ] **Step 4: Add `RecheckDnsProviderAsync`**

In `src/DotMarc/Components/Pages/DomainDetail.razor`, immediately after the existing `RecheckDkimAsync` method, add:
```csharp
    /// <summary>DNS provider counterpart to RecheckDmarcAsync - see its remarks.</summary>
    private async Task RecheckDnsProviderAsync()
    {
        _isRecheckingDnsProvider = true;
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            var domain = await db.Domains.SingleAsync(d => d.Id == _domain!.Id);
            try
            {
                await PollingService.RunSingleDnsProviderCheckAsync(domain, DnsProviderDetector, CancellationToken.None);
            }
            catch (Exception)
            {
                Snackbar.Add($"Failed to check {DomainName}'s DNS provider. Try again.", Severity.Error);
                return;
            }
            await db.SaveChangesAsync();

            _domain!.DnsProvider = domain.DnsProvider;
            _domain.DnsZone = domain.DnsZone;
            _domain.DnsProviderCheckedUtc = domain.DnsProviderCheckedUtc;
        }
        finally
        {
            _isRecheckingDnsProvider = false;
        }
    }
```

- [ ] **Step 5: Update the three push handlers that key off `DomainName`/`_domain` to read the persisted field**

In `src/DotMarc/Components/Pages/DomainDetail.razor`'s `PushDmarcRecordAsync`, change:
```csharp
            var detected = await DnsProviderDetector.DetectAsync(DomainName, CancellationToken.None);
            var providerKey = detected switch
            {
                DetectedDnsProvider.Cloudflare => "cloudflare",
                DetectedDnsProvider.AzureDns => "azure-dns",
                _ => null
            };
            var pushProvider = await DnsPushProviders.FindConfiguredAsync(providerKey);
            if (pushProvider is null)
            {
                Snackbar.Add("Couldn't find a configured DNS push option for this domain - add the record manually.", Severity.Warning);
                return;
            }

            DnsRecordLookupResult existing;
            try
            {
                existing = await DmarcTxtLookup.LookupAsync(DomainName, CancellationToken.None);
```
to:
```csharp
            var providerKey = _domain!.DnsProvider switch
            {
                DetectedDnsProvider.Cloudflare => "cloudflare",
                DetectedDnsProvider.AzureDns => "azure-dns",
                _ => null
            };
            var pushProvider = await DnsPushProviders.FindConfiguredAsync(providerKey);
            if (pushProvider is null)
            {
                Snackbar.Add("Couldn't find a configured DNS push option for this domain - add the record manually.", Severity.Warning);
                return;
            }

            DnsRecordLookupResult existing;
            try
            {
                existing = await DmarcTxtLookup.LookupAsync(DomainName, CancellationToken.None);
```

In `PushTlsrptRecordAsync`, change:
```csharp
        var detected = await DnsProviderDetector.DetectAsync(DomainName, CancellationToken.None);
        var providerKey = detected switch
        {
            DetectedDnsProvider.Cloudflare => "cloudflare",
            DetectedDnsProvider.AzureDns => "azure-dns",
            _ => null
        };
        var pushProvider = await DnsPushProviders.FindConfiguredAsync(providerKey);
```
to:
```csharp
        var providerKey = _domain!.DnsProvider switch
        {
            DetectedDnsProvider.Cloudflare => "cloudflare",
            DetectedDnsProvider.AzureDns => "azure-dns",
            _ => null
        };
        var pushProvider = await DnsPushProviders.FindConfiguredAsync(providerKey);
```

`PushDmarcAuthorizationRecordAsync` detects a DIFFERENT hostname (the mailbox's own domain, not `DomainName`), so it keeps a live `DetectAsync` call - only its return-type usage needs updating for Task 2's new `DnsProviderDetectionResult` shape. Change:
```csharp
            var detected = await DnsProviderDetector.DetectAsync(mailboxDomain, CancellationToken.None);
            var providerKey = detected switch
            {
                DetectedDnsProvider.Cloudflare => "cloudflare",
                DetectedDnsProvider.AzureDns => "azure-dns",
                _ => null
            };
```
to:
```csharp
            var detected = await DnsProviderDetector.DetectAsync(mailboxDomain, CancellationToken.None);
            var providerKey = detected.Provider switch
            {
                DetectedDnsProvider.Cloudflare => "cloudflare",
                DetectedDnsProvider.AzureDns => "azure-dns",
                _ => null
            };
```

- [ ] **Step 6: Update `DomainMtaStsPanel.razor`'s push handler the same way**

In `src/DotMarc/Components/Shared/DomainMtaStsPanel.razor`'s `PushDnsRecordsAsync`, change:
```csharp
            var detected = await DnsProviderDetector.DetectAsync(Domain.Name, CancellationToken.None);
            var providerKey = detected switch
            {
                DetectedDnsProvider.Cloudflare => "cloudflare",
                DetectedDnsProvider.AzureDns => "azure-dns",
                _ => null
            };
            var pushProvider = await DnsPushProviders.FindConfiguredAsync(providerKey);
            if (pushProvider is null)
            {
                Snackbar.Add($"Couldn't find a configured DNS push option for {Domain.Name} - add the CNAME manually.", Severity.Warning);
                return;
            }
```
to:
```csharp
            var providerKey = Domain.DnsProvider switch
            {
                DetectedDnsProvider.Cloudflare => "cloudflare",
                DetectedDnsProvider.AzureDns => "azure-dns",
                _ => null
            };
            var pushProvider = await DnsPushProviders.FindConfiguredAsync(providerKey);
            if (pushProvider is null)
            {
                Snackbar.Add($"Couldn't find a configured DNS push option for {Domain.Name} - add the CNAME manually.", Severity.Warning);
                return;
            }
```
`DomainMtaStsPanel`'s `@inject IDnsProviderDetector DnsProviderDetector` (line 18) is no longer used by this handler after this change - leave the injection in place, since it costs nothing and removing an unused injection is outside this task's scope. (If a build warning flags it as unused, that's fine to leave; MudBlazor/Blazor `@inject` directives don't typically trigger unused-field warnings the way a plain C# field would.)

- [ ] **Step 7: Build**

Run: `dotnet build`
Expected: builds cleanly, no errors. This is the step where the compile errors left dangling since Task 2 finally resolve.

- [ ] **Step 8: Run the full test suite**

Run: `dotnet test`
Expected: PASS, all tests green (no bUnit component tests exist for either modified file - build success plus the tests from Tasks 1-4 are the coverage for this change).

- [ ] **Step 9: Manually verify in the browser**

Start the app locally (`dotnet exec bin/Debug/net10.0/DotMarc.dll` from `src/DotMarc/`, per this project's Windows Defender workaround for freshly-built `.exe`s - or `dotnet run` if that's not an issue in the current environment). Open a domain that is itself a DNS zone apex (e.g. any existing monitored domain) and confirm: the header badge and the "DNS provider" health row both show a provider and zone matching the domain name itself; the recheck button works; existing push buttons (DMARC/TLSRPT/MTA-STS) still work exactly as before. If a real subdomain-of-a-larger-zone domain is available to monitor (matching the original bug report's shape), confirm the header badge and health row show the PARENT zone's name, and that a push (if a provider is configured for this deployment) now succeeds instead of failing with "Couldn't find a configured DNS push option."

Expected: all behaviors match. Note in the final report whether this manual check could be completed.

- [ ] **Step 10: Commit**

```bash
git add src/DotMarc/Components/Pages/DomainDetail.razor src/DotMarc/Components/Shared/DomainMtaStsPanel.razor
git commit -m "Show detected DNS provider/zone and use it for push-button provider selection"
```

---

## Final Verification

- [ ] Run the full test suite one more time: `dotnet test` - expect PASS, no failures.
- [ ] Run `dotnet build` - expect a clean build with no new warnings.
- [ ] Confirm the migration from Task 1 applies cleanly to a fresh database (already exercised by every Postgres-backed test class's `InitializeAsync`, but worth a final sanity check if anything about the migration was hand-edited in Task 1 Step 7).
- [ ] Skim the spec's Non-goals once more: no change to either push provider's record-writing code (confirmed - neither `AzureDnsPushProvider.cs` nor `CloudflareDnsPushProvider.cs` appears in any task's file list), no Public Suffix List, no new supported providers.
