# DNS Provider Push Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user push the MTA-STS CNAME or the DMARC `_dmarc` TXT record straight to Cloudflare
or Azure DNS, authenticated via a fresh OAuth consent round-trip each time, with nothing ever
persisted.

**Architecture:** A provider-agnostic core (`DnsRecordChange`, `IDnsProviderDetector`,
`DmarcRuaMerge`) that's pure and unit-tested, feeding two `IDnsPushProvider` implementations
(Cloudflare, Azure DNS) that do the actual OAuth exchange and API call — verified live, not mocked,
same acceptance already made for `AzureMtaStsHostProvisioner`/`MxHostsLookup`. Two new minimal-API
endpoints in `Program.cs` (`/dns-push/{provider}/start` and `.../callback`) carry a signed,
short-lived `state` parameter so no server-side session is needed across the redirect.

**Tech Stack:** .NET 10, Blazor Server, MudBlazor, EF Core/Npgsql, `Microsoft.Identity.Client`
(already a dependency), `Azure.ResourceManager.Dns` (new dependency), ASP.NET Core Data Protection.

**Spec:** `docs/superpowers/specs/2026-09-01-dns-provider-push-design.md`

## Global Constraints

- No provider credential, access token, or refresh token is ever written to the database, a cache,
  or disk — every push re-authenticates from scratch (spec, Non-goals).
- Cloudflare OAuth: authorization endpoint `https://dash.cloudflare.com/oauth2/auth`, token endpoint
  `https://dash.cloudflare.com/oauth2/token`, PKCE `S256`, no `offline_access` scope requested
  (spec, Auth model).
- Azure DNS: a *third*, separate Entra app registration from the existing mailbox and dashboard
  apps — never reuse one (spec, Auth model; matches the existing precedent in
  `website/docs/getting-started.mdx`).
- Cloudflare NS suffix → `.ns.cloudflare.com`. Azure DNS NS suffixes → `.azure-dns.com`,
  `.azure-dns.net`, `.azure-dns.org`, `.azure-dns.info` (spec, Provider detection).
- A misconfigured `_dmarc` TXT record is never overwritten silently — always a before/after diff
  with explicit confirmation (spec, Goals).
- Where detection fails, the provider isn't Cloudflare/Azure DNS, or that provider's OAuth app
  isn't configured: no push button renders, today's manual instructions are unchanged (spec,
  Non-goals).
- TDD throughout: RED (failing test) → GREEN (minimal implementation) → commit, for every task that
  has pure/testable logic. OAuth exchange and provider API calls are the one documented exception —
  verified live after implementation, not unit-tested (spec, Testing).

---

## Task 1: Add the Azure.ResourceManager.Dns package

**Files:**
- Modify: `src/DotMarc/DotMarc.csproj`

**Interfaces:**
- Produces: the `Azure.ResourceManager.Dns` and `Azure.ResourceManager.Dns.Models` namespaces,
  needed by Task 9 (`AzureDnsPushProvider`).

- [ ] **Step 1: Add the package reference**

In `src/DotMarc/DotMarc.csproj`, inside the existing `<ItemGroup>` with the other
`Azure.ResourceManager.*` packages, add:

```xml
<PackageReference Include="Azure.ResourceManager.Dns" Version="1.1.1" />
```

- [ ] **Step 2: Restore and build**

Run: `dotnet build src/DotMarc/DotMarc.csproj`
Expected: succeeds, 0 errors (nothing references the new package yet).

- [ ] **Step 3: Commit**

```bash
git add src/DotMarc/DotMarc.csproj
git commit -m "Add Azure.ResourceManager.Dns package reference"
```

---

## Task 2: DnsRecordChange model

**Files:**
- Create: `src/DotMarc/DnsPush/DnsRecordChange.cs`

**Interfaces:**
- Produces: `DnsRecordChangeKind` (enum: `Create`, `Merge`), `DnsRecordChange` record with
  properties `Kind`, `RecordType` (string), `Name` (string), `DesiredValue` (string),
  `ExistingValue` (string?) — consumed by every later task that builds or pushes a record change.

This is a plain data model with no behavior, so there's no test to write — just create the file.

- [ ] **Step 1: Create the file**

```csharp
namespace DotMarc.DnsPush;

public enum DnsRecordChangeKind { Create, Merge }

/// <summary>One DNS record change to push, independent of which provider ends up handling it.
/// ExistingValue is set only for Kind == Merge, where the pushed value replaces (not appends to)
/// whatever's currently live — see DmarcRuaMerge for how that value is actually built.</summary>
public sealed record DnsRecordChange(
    DnsRecordChangeKind Kind,
    string RecordType,
    string Name,
    string DesiredValue,
    string? ExistingValue);
```

- [ ] **Step 2: Build**

Run: `dotnet build src/DotMarc/DotMarc.csproj`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/DotMarc/DnsPush/DnsRecordChange.cs
git commit -m "Add DnsRecordChange model"
```

---

## Task 3: DmarcRuaMerge

**Files:**
- Create: `src/DotMarc/DnsPush/DmarcRuaMerge.cs`
- Test: `test/DotMarc.Tests/DnsPush/DmarcRuaMergeTests.cs`

**Interfaces:**
- Consumes: nothing (pure function).
- Produces: `DmarcRuaMerge.TryMerge(string existingValue, string mailboxAddress) : string?` —
  consumed by Task 11 (DomainDetail.razor's diff dialog) and the callback endpoint in Task 10.

- [ ] **Step 1: Write the failing tests**

```csharp
// test/DotMarc.Tests/DnsPush/DmarcRuaMergeTests.cs
using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DmarcRuaMergeTests
{
    [Fact]
    public void TryMerge_ReplacesAnExistingRuaTag_PreservingOtherTags()
    {
        var result = DmarcRuaMerge.TryMerge("v=DMARC1; p=quarantine; rua=mailto:wrong@example.com; sp=reject", "correct@mjco.uk");

        Assert.Equal("v=DMARC1; p=quarantine; rua=mailto:correct@mjco.uk; sp=reject", result);
    }

    [Fact]
    public void TryMerge_AppendsRuaTag_WhenNoneExists()
    {
        var result = DmarcRuaMerge.TryMerge("v=DMARC1; p=quarantine", "correct@mjco.uk");

        Assert.Equal("v=DMARC1; p=quarantine; rua=mailto:correct@mjco.uk", result);
    }

    [Fact]
    public void TryMerge_ReturnsNull_WhenExistingValueIsNotADmarcRecord()
    {
        var result = DmarcRuaMerge.TryMerge("some unrelated txt record", "correct@mjco.uk");

        Assert.Null(result);
    }

    [Fact]
    public void TryMerge_IsCaseInsensitive_OnTheVersionTag()
    {
        var result = DmarcRuaMerge.TryMerge("v=dmarc1; p=none", "correct@mjco.uk");

        Assert.Equal("v=dmarc1; p=none; rua=mailto:correct@mjco.uk", result);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DmarcRuaMergeTests`
Expected: FAIL — `DmarcRuaMerge` does not exist.

- [ ] **Step 3: Implement**

```csharp
// src/DotMarc/DnsPush/DmarcRuaMerge.cs
namespace DotMarc.DnsPush;

/// <summary>Replaces (or appends) the rua= tag in an existing _dmarc TXT record's value, leaving
/// every other tag untouched — pushing a fix for DmarcCheckStatus.Misconfigured must not silently
/// discard tags (sp=, pct=, adkim=, etc.) a customer set on purpose.</summary>
public static class DmarcRuaMerge
{
    /// <summary>Returns the merged value, or null if <paramref name="existingValue"/> doesn't even
    /// start with "v=DMARC1" — not safe to merge into; the caller should offer a full-replacement
    /// warning instead.</summary>
    public static string? TryMerge(string existingValue, string mailboxAddress)
    {
        if (!existingValue.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var tags = existingValue
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        var ruaIndex = tags.FindIndex(t => t.StartsWith("rua=", StringComparison.OrdinalIgnoreCase));
        var ruaTag = $"rua=mailto:{mailboxAddress}";

        if (ruaIndex >= 0)
        {
            tags[ruaIndex] = ruaTag;
        }
        else
        {
            tags.Add(ruaTag);
        }

        return string.Join("; ", tags);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DmarcRuaMergeTests`
Expected: PASS, 4/4.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/DnsPush/DmarcRuaMerge.cs test/DotMarc.Tests/DnsPush/DmarcRuaMergeTests.cs
git commit -m "Add DmarcRuaMerge for surgical rua= tag repair"
```

---

## Task 4: DetectedDnsProvider + IDnsProviderDetector + DnsProviderDetector

**Files:**
- Create: `src/DotMarc/DnsPush/DetectedDnsProvider.cs`
- Create: `src/DotMarc/DnsPush/IDnsProviderDetector.cs`
- Create: `src/DotMarc/DnsPush/DnsProviderDetector.cs`
- Test: `test/DotMarc.Tests/DnsPush/DnsProviderDetectorTests.cs`

**Interfaces:**
- Consumes: `HttpClient` (constructor-injected, same shape as `MxHostsLookup`/`MtaStsDnsVerifier`).
- Produces: `DetectedDnsProvider` (enum: `Unknown`, `Cloudflare`, `AzureDns`),
  `IDnsProviderDetector.DetectAsync(string domainName, CancellationToken) : Task<DetectedDnsProvider>`
  — consumed by Task 10 (ManageMtaSts.razor) and Task 11 (DomainDetail.razor).

- [ ] **Step 1: Write the failing tests**

```csharp
// test/DotMarc.Tests/DnsPush/DnsProviderDetectorTests.cs
using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DnsProviderDetectorTests
{
    private static (DnsProviderDetector detector, FakeHttpMessageHandler handler) CreateDetector()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new DnsProviderDetector(http), handler);
    }

    [Fact]
    public async Task DetectAsync_ReturnsCloudflare_WhenNsRecordsAreCloudflares()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":2,"data":"ana.ns.cloudflare.com."},{"type":2,"data":"bob.ns.cloudflare.com."}]}
            """;

        var result = await detector.DetectAsync("contoso.io", CancellationToken.None);

        Assert.Equal(DetectedDnsProvider.Cloudflare, result);
    }

    [Theory]
    [InlineData("ns1-01.azure-dns.com.")]
    [InlineData("ns2-01.azure-dns.net.")]
    [InlineData("ns3-01.azure-dns.org.")]
    [InlineData("ns4-01.azure-dns.info.")]
    public async Task DetectAsync_ReturnsAzureDns_ForEachAzureDnsSuffix(string nsHost)
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBody = $$"""
            {"Status":0,"Answer":[{"type":2,"data":"{{nsHost}}"}]}
            """;

        var result = await detector.DetectAsync("contoso.io", CancellationToken.None);

        Assert.Equal(DetectedDnsProvider.AzureDns, result);
    }

    [Fact]
    public async Task DetectAsync_ReturnsUnknown_ForAnUnrecognizedProvider()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":2,"data":"dns1.registrar-nameservers.com."}]}
            """;

        var result = await detector.DetectAsync("contoso.io", CancellationToken.None);

        Assert.Equal(DetectedDnsProvider.Unknown, result);
    }

    [Fact]
    public async Task DetectAsync_ReturnsUnknown_WhenNoNsRecordsExist()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBody = """{"Status":3}""";

        var result = await detector.DetectAsync("contoso.io", CancellationToken.None);

        Assert.Equal(DetectedDnsProvider.Unknown, result);
    }

    [Fact]
    public async Task DetectAsync_QueriesNsRecordType_ForTheGivenDomain()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBody = """{"Status":3}""";

        await detector.DetectAsync("contoso.io", CancellationToken.None);

        Assert.Contains("contoso.io", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("type=NS", handler.Requests[0].RequestUri!.ToString());
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DnsProviderDetectorTests`
Expected: FAIL — `DnsProviderDetector` does not exist.

- [ ] **Step 3: Implement**

```csharp
// src/DotMarc/DnsPush/DetectedDnsProvider.cs
namespace DotMarc.DnsPush;

public enum DetectedDnsProvider { Unknown, Cloudflare, AzureDns }
```

```csharp
// src/DotMarc/DnsPush/IDnsProviderDetector.cs
namespace DotMarc.DnsPush;

public interface IDnsProviderDetector
{
    Task<DetectedDnsProvider> DetectAsync(string domainName, CancellationToken cancellationToken);
}
```

```csharp
// src/DotMarc/DnsPush/DnsProviderDetector.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.DnsPush;

/// <summary>Detects whether a domain's DNS is hosted on Cloudflare or Azure DNS by matching its NS
/// records' hostnames against each provider's well-known name server suffixes. Queries Cloudflare's
/// own DNS-over-HTTPS JSON API, same approach as DmarcDnsChecker/MtaStsDnsVerifier.</summary>
public sealed class DnsProviderDetector : IDnsProviderDetector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] CloudflareNsSuffixes = [".ns.cloudflare.com"];
    private static readonly string[] AzureDnsNsSuffixes =
        [".azure-dns.com", ".azure-dns.net", ".azure-dns.org", ".azure-dns.info"];

    private readonly HttpClient _http;

    public DnsProviderDetector(HttpClient http) => _http = http;

    public async Task<DetectedDnsProvider> DetectAsync(string domainName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(domainName)}&type=NS");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;

        var nsHosts = (parsed.Answer ?? []).Where(a => a.Type == 2).Select(a => a.Data.TrimEnd('.'));

        foreach (var host in nsHosts)
        {
            if (CloudflareNsSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                return DetectedDnsProvider.Cloudflare;
            }
            if (AzureDnsNsSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                return DetectedDnsProvider.AzureDns;
            }
        }

        return DetectedDnsProvider.Unknown;
    }

    private sealed record DnsOverHttpsResponse(
        [property: JsonPropertyName("Status")] int Status,
        [property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer(
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("data")] string Data);
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DnsProviderDetectorTests`
Expected: PASS, 8/8.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/DnsPush/DetectedDnsProvider.cs src/DotMarc/DnsPush/IDnsProviderDetector.cs src/DotMarc/DnsPush/DnsProviderDetector.cs test/DotMarc.Tests/DnsPush/DnsProviderDetectorTests.cs
git commit -m "Add DNS provider detection via NS record pattern matching"
```

---

## Task 5: DmarcTxtLookup

**Files:**
- Create: `src/DotMarc/DnsPush/IDmarcTxtLookup.cs`
- Create: `src/DotMarc/DnsPush/DmarcTxtLookup.cs`
- Test: `test/DotMarc.Tests/DnsPush/DmarcTxtLookupTests.cs`

**Interfaces:**
- Consumes: `HttpClient`.
- Produces: `IDmarcTxtLookup.LookupAsync(string domainName, CancellationToken) : Task<string?>` —
  the raw, live `_dmarc.<domain>` TXT value, or null if none exists. Consumed by the callback
  endpoint (Task 10) and DomainDetail.razor's diff preview (Task 11).

Fetches the record fresh at push time rather than trusting whatever was true when a button
rendered — if the record changed in between, the merge happens against what's actually live.

- [ ] **Step 1: Write the failing tests**

```csharp
// test/DotMarc.Tests/DnsPush/DmarcTxtLookupTests.cs
using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DmarcTxtLookupTests
{
    private static (DmarcTxtLookup lookup, FakeHttpMessageHandler handler) CreateLookup()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new DmarcTxtLookup(http), handler);
    }

    [Fact]
    public async Task LookupAsync_ReturnsNull_WhenNoRecordExists()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """{"Status":3}""";

        var result = await lookup.LookupAsync("contoso.io", CancellationToken.None);

        Assert.Null(result);
        Assert.Contains("_dmarc.contoso.io", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task LookupAsync_ReturnsTheUnquotedValue()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk\""}]}
            """;

        var result = await lookup.LookupAsync("contoso.io", CancellationToken.None);

        Assert.Equal("v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk", result);
    }

    [Fact]
    public async Task LookupAsync_JoinsMultiSegmentValues()
    {
        var (lookup, handler) = CreateLookup();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; \" \"rua=mailto:rua.dmarc@mjco.uk\""}]}
            """;

        var result = await lookup.LookupAsync("contoso.io", CancellationToken.None);

        Assert.Equal("v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk", result);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DmarcTxtLookupTests`
Expected: FAIL — `DmarcTxtLookup` does not exist.

- [ ] **Step 3: Implement**

```csharp
// src/DotMarc/DnsPush/IDmarcTxtLookup.cs
namespace DotMarc.DnsPush;

public interface IDmarcTxtLookup
{
    Task<string?> LookupAsync(string domainName, CancellationToken cancellationToken);
}
```

```csharp
// src/DotMarc/DnsPush/DmarcTxtLookup.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.DnsPush;

/// <summary>Fetches the raw, currently-live _dmarc.&lt;domain&gt; TXT record value — used only by
/// the DMARC push flow, to decide Create vs. Merge and build the merged value against whatever's
/// live right now. Mirrors DmarcDnsChecker's own TXT-fetching logic rather than sharing code with
/// it, matching this codebase's existing MxHostsLookup/MtaStsDnsVerifier precedent of small,
/// independent DNS-over-HTTPS callers over a shared abstraction.</summary>
public sealed class DmarcTxtLookup : IDmarcTxtLookup
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public DmarcTxtLookup(HttpClient http) => _http = http;

    public async Task<string?> LookupAsync(string domainName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString($"_dmarc.{domainName}")}&type=TXT");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;

        var answer = parsed.Answer?.FirstOrDefault(a => a.Type == 16);
        return answer is null ? null : string.Join("", answer.Data.Split("\" \"")).Trim('"');
    }

    private sealed record DnsOverHttpsResponse(
        [property: JsonPropertyName("Status")] int Status,
        [property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer(
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("data")] string Data);
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DmarcTxtLookupTests`
Expected: PASS, 3/3.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/DnsPush/IDmarcTxtLookup.cs src/DotMarc/DnsPush/DmarcTxtLookup.cs test/DotMarc.Tests/DnsPush/DmarcTxtLookupTests.cs
git commit -m "Add DmarcTxtLookup for fetching the live _dmarc TXT value"
```

---

## Task 6: PkceGenerator + DnsPushState + DnsPushStateProtector

**Files:**
- Create: `src/DotMarc/DnsPush/PkceGenerator.cs`
- Create: `src/DotMarc/DnsPush/DnsPushState.cs`
- Create: `src/DotMarc/DnsPush/DnsPushStateProtector.cs`
- Test: `test/DotMarc.Tests/DnsPush/DnsPushStateProtectorTests.cs`

**Interfaces:**
- Produces: `PkceGenerator.Generate() : (string CodeVerifier, string CodeChallenge)`;
  `DnsPushState` record (`DomainId` int, `PushTarget` string, `CodeVerifier` string,
  `ExpiresAtUtc` DateTimeOffset); `DnsPushStateProtector.Protect(int domainId, string pushTarget,
  string codeVerifier, DateTimeOffset nowUtc) : string` and
  `DnsPushStateProtector.Unprotect(string protectedState, DateTimeOffset nowUtc) : DnsPushState?`
  — consumed by the two minimal-API endpoints in Task 10.

- [ ] **Step 1: Write the failing tests**

```csharp
// test/DotMarc.Tests/DnsPush/DnsPushStateProtectorTests.cs
using DotMarc.DnsPush;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DnsPushStateProtectorTests
{
    private static DnsPushStateProtector CreateProtector() =>
        new(DataProtectionProvider.Create("DotMarc.Tests"));

    [Fact]
    public void Protect_ThenUnprotect_RoundTripsTheOriginalValues()
    {
        var protector = CreateProtector();
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        var protectedState = protector.Protect(42, "mta-sts", "test-verifier", now);
        var result = protector.Unprotect(protectedState, now);

        Assert.NotNull(result);
        Assert.Equal(42, result!.DomainId);
        Assert.Equal("mta-sts", result.PushTarget);
        Assert.Equal("test-verifier", result.CodeVerifier);
    }

    [Fact]
    public void Unprotect_ReturnsNull_ForATamperedValue()
    {
        var protector = CreateProtector();
        var now = DateTimeOffset.UtcNow;
        var protectedState = protector.Protect(42, "mta-sts", "test-verifier", now);

        var result = protector.Unprotect(protectedState + "tampered", now);

        Assert.Null(result);
    }

    [Fact]
    public void Unprotect_ReturnsNull_OnceExpired()
    {
        var protector = CreateProtector();
        var issuedAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var protectedState = protector.Protect(42, "mta-sts", "test-verifier", issuedAt);

        var result = protector.Unprotect(protectedState, issuedAt.AddMinutes(10));

        Assert.Null(result);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DnsPushStateProtectorTests`
Expected: FAIL — `DnsPushStateProtector` does not exist.

- [ ] **Step 3: Implement**

```csharp
// src/DotMarc/DnsPush/PkceGenerator.cs
using System.Security.Cryptography;
using System.Text;

namespace DotMarc.DnsPush;

/// <summary>Generates a PKCE code_verifier/code_challenge pair (RFC 7636, S256 method) for the
/// OAuth authorization-code exchange — used even for these confidential/server-side clients as
/// defense in depth on the code exchange, per the design spec.</summary>
public static class PkceGenerator
{
    public static (string CodeVerifier, string CodeChallenge) Generate()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var codeVerifier = Base64UrlEncode(verifierBytes);

        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var codeChallenge = Base64UrlEncode(challengeBytes);

        return (codeVerifier, codeChallenge);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
```

```csharp
// src/DotMarc/DnsPush/DnsPushState.cs
namespace DotMarc.DnsPush;

/// <summary>PushTarget is "mta-sts" or "dmarc" — which record kind this push is for. Deliberately
/// carries no record VALUE: the callback endpoint re-derives what to push at push time (see
/// DmarcTxtLookup's doc comment for why), so this only needs enough to know which domain and which
/// flow, plus the PKCE verifier the /start step generated.</summary>
public sealed record DnsPushState(
    int DomainId,
    string PushTarget,
    string CodeVerifier,
    DateTimeOffset ExpiresAtUtc);
```

```csharp
// src/DotMarc/DnsPush/DnsPushStateProtector.cs
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace DotMarc.DnsPush;

/// <summary>Encodes a DnsPushState into an opaque, tamper-proof string carried as the OAuth `state`
/// parameter across the redirect to the provider and back — avoids needing any server-side session
/// between /dns-push/{provider}/start and .../callback. Short-lived (5 minutes): a state value used
/// after that window is rejected, same reasoning as an OIDC nonce.</summary>
public sealed class DnsPushStateProtector
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly IDataProtector _protector;

    public DnsPushStateProtector(IDataProtectionProvider dataProtectionProvider) =>
        _protector = dataProtectionProvider.CreateProtector("DotMarc.DnsPush.State.v1");

    public string Protect(int domainId, string pushTarget, string codeVerifier, DateTimeOffset nowUtc)
    {
        var state = new DnsPushState(domainId, pushTarget, codeVerifier, nowUtc.Add(Lifetime));
        return _protector.Protect(JsonSerializer.Serialize(state));
    }

    /// <summary>Returns null if the value is malformed, was tampered with, or has expired.</summary>
    public DnsPushState? Unprotect(string protectedState, DateTimeOffset nowUtc)
    {
        string json;
        try
        {
            json = _protector.Unprotect(protectedState);
        }
        catch (CryptographicException)
        {
            return null;
        }

        var state = JsonSerializer.Deserialize<DnsPushState>(json);
        return state is not null && state.ExpiresAtUtc > nowUtc ? state : null;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DnsPushStateProtectorTests`
Expected: PASS, 3/3.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/DnsPush/PkceGenerator.cs src/DotMarc/DnsPush/DnsPushState.cs src/DotMarc/DnsPush/DnsPushStateProtector.cs test/DotMarc.Tests/DnsPush/DnsPushStateProtectorTests.cs
git commit -m "Add PKCE generation and signed DNS push state round-tripping"
```

---

## Task 7: IDnsPushProvider + DnsPushResult + CloudflareDnsOptions

**Files:**
- Create: `src/DotMarc/DnsPush/IDnsPushProvider.cs`
- Create: `src/DotMarc/DnsPush/CloudflareDnsOptions.cs`

**Interfaces:**
- Produces: `DnsPushOutcome` (enum: `Pushed`, `ZoneNotFound`, `ProviderError`), `DnsPushResult`
  record (`Outcome`, `DetailMessage` string?), `IDnsPushProvider` interface (`ProviderKey` string,
  `IsConfigured` bool, `BuildAuthorizationUrl(string state, string codeChallenge, string
  redirectUri) : string`, `ExchangeAndPushAsync(string code, string codeVerifier, string
  redirectUri, DnsRecordChange change, CancellationToken) : Task<DnsPushResult>`) —
  `CloudflareDnsPushProvider` (Task 8) and `AzureDnsPushProvider` (Task 9) implement this.
  `CloudflareDnsOptions` (`ClientId` string?, `ClientSecret` string?) is bound from config in
  Task 10.

No test here — this is an interface and a plain options class, same as `MtaStsOptions`.

- [ ] **Step 1: Create the files**

```csharp
// src/DotMarc/DnsPush/IDnsPushProvider.cs
namespace DotMarc.DnsPush;

public enum DnsPushOutcome { Pushed, ZoneNotFound, ProviderError }

public sealed record DnsPushResult(DnsPushOutcome Outcome, string? DetailMessage);

/// <summary>One implementation per supported DNS provider. Every method is stateless from
/// dotMARC's own perspective — nothing about the OAuth exchange is ever persisted; the access token
/// exists only as a local variable for the duration of ExchangeAndPushAsync.</summary>
public interface IDnsPushProvider
{
    /// <summary>Matches DetectedDnsProvider and the {provider} route segment in
    /// /dns-push/{provider}/start|callback — "cloudflare" or "azure-dns".</summary>
    string ProviderKey { get; }

    /// <summary>False when this provider's OAuth app isn't configured for this deployment — the
    /// push button never renders in that case.</summary>
    bool IsConfigured { get; }

    string BuildAuthorizationUrl(string state, string codeChallenge, string redirectUri);

    Task<DnsPushResult> ExchangeAndPushAsync(
        string code, string codeVerifier, string redirectUri, DnsRecordChange change, CancellationToken cancellationToken);
}
```

```csharp
// src/DotMarc/DnsPush/CloudflareDnsOptions.cs
namespace DotMarc.DnsPush;

/// <summary>Optional — a deployment that never registers a Cloudflare OAuth client simply never
/// shows the "Push via Cloudflare" button (see CloudflareDnsPushProvider.IsConfigured).</summary>
public sealed class CloudflareDnsOptions
{
    public const string SectionName = "CloudflareDns";

    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/DotMarc/DotMarc.csproj`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/DotMarc/DnsPush/IDnsPushProvider.cs src/DotMarc/DnsPush/CloudflareDnsOptions.cs
git commit -m "Add IDnsPushProvider abstraction and CloudflareDnsOptions"
```

---

## Task 8: CloudflareDnsPushProvider

**Files:**
- Create: `src/DotMarc/DnsPush/CloudflareDnsPushProvider.cs`

**Interfaces:**
- Consumes: `IOptions<CloudflareDnsOptions>`, `HttpClient` (constructor-injected, no fixed
  `BaseAddress` — it calls both `dash.cloudflare.com` and `api.cloudflare.com`).
- Produces: `CloudflareDnsPushProvider : IDnsPushProvider` — registered in Task 10.

No automated test for this task: the OAuth exchange and Cloudflare API calls are exactly the kind
of external I/O this codebase already accepts as live-verified-only (see
`AzureMtaStsHostProvisioner`, `MtaStsHostProvisioner`'s Azure implementation). Verification happens
manually once a real Cloudflare OAuth client is registered (see Task 12's docs).

- [ ] **Step 1: Implement**

```csharp
// src/DotMarc/DnsPush/CloudflareDnsPushProvider.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace DotMarc.DnsPush;

/// <summary>Pushes a DNS record change to Cloudflare, authenticated via a fresh OAuth 2.0
/// Authorization Code + PKCE exchange each time — see the design spec's "Auth model" section for
/// why nothing is ever persisted. Endpoints confirmed against Cloudflare's own OIDC discovery
/// document (https://dash.cloudflare.com/.well-known/openid-configuration); the DNS API itself is
/// documented at https://developers.cloudflare.com/api/resources/dns/subresources/records/.</summary>
public sealed class CloudflareDnsPushProvider : IDnsPushProvider
{
    private const string AuthorizationEndpoint = "https://dash.cloudflare.com/oauth2/auth";
    private const string TokenEndpoint = "https://dash.cloudflare.com/oauth2/token";
    private const string ApiBase = "https://api.cloudflare.com/client/v4";

    private readonly CloudflareDnsOptions _options;
    private readonly HttpClient _http;

    public CloudflareDnsPushProvider(IOptions<CloudflareDnsOptions> options, HttpClient http)
    {
        _options = options.Value;
        _http = http;
    }

    public string ProviderKey => "cloudflare";
    public bool IsConfigured => !string.IsNullOrEmpty(_options.ClientId) && !string.IsNullOrEmpty(_options.ClientSecret);

    public string BuildAuthorizationUrl(string state, string codeChallenge, string redirectUri)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = _options.ClientId!,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "com.cloudflare.api.account.zone.dns",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };
        return AuthorizationEndpoint + "?" + string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    public async Task<DnsPushResult> ExchangeAndPushAsync(
        string code, string codeVerifier, string redirectUri, DnsRecordChange change, CancellationToken cancellationToken)
    {
        var accessToken = await ExchangeCodeForTokenAsync(code, codeVerifier, redirectUri, cancellationToken).ConfigureAwait(false);
        if (accessToken is null)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, "Cloudflare rejected the authorization code exchange.");
        }

        var zoneName = ZoneNameFor(change.Name);
        var zoneId = await FindZoneIdAsync(zoneName, accessToken, cancellationToken).ConfigureAwait(false);
        if (zoneId is null)
        {
            return new DnsPushResult(DnsPushOutcome.ZoneNotFound, $"Couldn't find {zoneName} in the Cloudflare account you authorized.");
        }

        return change.Kind == DnsRecordChangeKind.Merge
            ? await UpdateExistingRecordAsync(zoneId, accessToken, change, cancellationToken).ConfigureAwait(false)
            : await CreateRecordAsync(zoneId, accessToken, change, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ExchangeCodeForTokenAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = _options.ClientId!,
                ["client_secret"] = _options.ClientSecret!,
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

    private async Task<string?> FindZoneIdAsync(string zoneName, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/zones?name={Uri.EscapeDataString(zoneName)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var zones = await response.Content.ReadFromJsonAsync<ApiResponse<List<IdRecord>>>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return zones?.Result?.FirstOrDefault()?.Id;
    }

    private async Task<DnsPushResult> CreateRecordAsync(string zoneId, string accessToken, DnsRecordChange change, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/zones/{zoneId}/dns_records")
        {
            Content = JsonContent.Create(new DnsRecordPayload(change.RecordType, change.Name, change.DesiredValue))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? new DnsPushResult(DnsPushOutcome.Pushed, null)
            : new DnsPushResult(DnsPushOutcome.ProviderError, $"Cloudflare rejected the record push ({(int)response.StatusCode}).");
    }

    private async Task<DnsPushResult> UpdateExistingRecordAsync(string zoneId, string accessToken, DnsRecordChange change, CancellationToken cancellationToken)
    {
        using var findRequest = new HttpRequestMessage(HttpMethod.Get,
            $"{ApiBase}/zones/{zoneId}/dns_records?type={change.RecordType}&name={Uri.EscapeDataString(change.Name)}");
        findRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var findResponse = await _http.SendAsync(findRequest, cancellationToken).ConfigureAwait(false);
        var existing = await findResponse.Content.ReadFromJsonAsync<ApiResponse<List<IdRecord>>>(cancellationToken: cancellationToken).ConfigureAwait(false);
        var recordId = existing?.Result?.FirstOrDefault()?.Id;
        if (recordId is null)
        {
            return new DnsPushResult(DnsPushOutcome.ZoneNotFound, $"{change.Name} no longer exists at Cloudflare — it may have been removed since this page loaded.");
        }

        using var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"{ApiBase}/zones/{zoneId}/dns_records/{recordId}")
        {
            Content = JsonContent.Create(new DnsRecordPayload(change.RecordType, change.Name, change.DesiredValue))
        };
        updateRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var updateResponse = await _http.SendAsync(updateRequest, cancellationToken).ConfigureAwait(false);
        return updateResponse.IsSuccessStatusCode
            ? new DnsPushResult(DnsPushOutcome.Pushed, null)
            : new DnsPushResult(DnsPushOutcome.ProviderError, $"Cloudflare rejected the record update ({(int)updateResponse.StatusCode}).");
    }

    /// <summary>dotMARC only ever calls this with a name of the form "mta-sts.&lt;domain&gt;" or
    /// "_dmarc.&lt;domain&gt;", so stripping the first label always yields the zone name — this
    /// would not generalize to arbitrary multi-label zones, and doesn't need to.</summary>
    private static string ZoneNameFor(string recordName)
    {
        var firstDot = recordName.IndexOf('.');
        return firstDot < 0 ? recordName : recordName[(firstDot + 1)..];
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);
    private sealed record ApiResponse<T>([property: JsonPropertyName("result")] T? Result);
    private sealed record IdRecord([property: JsonPropertyName("id")] string Id);
    private sealed record DnsRecordPayload(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("content")] string Content);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/DotMarc/DotMarc.csproj`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/DotMarc/DnsPush/CloudflareDnsPushProvider.cs
git commit -m "Add CloudflareDnsPushProvider"
```

---

## Task 9: AzureDnsOptions + AzureDnsPushProvider

**Files:**
- Create: `src/DotMarc/DnsPush/AzureDnsOptions.cs`
- Create: `src/DotMarc/DnsPush/AzureDnsPushProvider.cs`

**Interfaces:**
- Consumes: `IOptions<AzureDnsOptions>`.
- Produces: `AzureDnsPushProvider : IDnsPushProvider` — registered in Task 10.

Same live-verification acceptance as Task 8. **Note before starting:** this task uses
`Azure.ResourceManager.Dns`'s record-set model types (`DnsCnameRecordData`, `DnsTxtRecordData`, and
their access via `DnsZoneResource`) from memory of the SDK's usual shape, the same way
`AzureMtaStsHostProvisioner` was written against `Azure.ResourceManager.AppContainers` earlier in
this project. If a type or member name doesn't match what `Azure.ResourceManager.Dns` 1.1.1 (added
in Task 1) actually exposes, `dotnet build`'s error will name the real one — adjust to match rather
than treating this as a blocker; this is expected, normal iteration against a real SDK, not a
design problem.

- [ ] **Step 1: Implement**

```csharp
// src/DotMarc/DnsPush/AzureDnsOptions.cs
namespace DotMarc.DnsPush;

/// <summary>Optional — a deployment that never registers this Entra app simply never shows the
/// "Push via Azure DNS" button. A THIRD, separate app registration from the existing mailbox and
/// dashboard ones (see getting-started.mdx) — never reuse an app registration across purposes.</summary>
public sealed class AzureDnsOptions
{
    public const string SectionName = "AzureDns";

    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}
```

```csharp
// src/DotMarc/DnsPush/AzureDnsPushProvider.cs
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Dns;
using Azure.ResourceManager.Dns.Models;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace DotMarc.DnsPush;

/// <summary>Pushes a DNS record change to Azure DNS via a delegated Entra ID authorization-code
/// exchange — the push only succeeds if the SIGNED-IN USER's own Azure RBAC grants them write
/// access on the target zone; dotMARC never holds a standing grant of its own. Same "nothing
/// persisted" contract as CloudflareDnsPushProvider.</summary>
public sealed class AzureDnsPushProvider : IDnsPushProvider
{
    private const string Scope = "https://management.azure.com/user_impersonation";

    private readonly AzureDnsOptions _options;

    public AzureDnsPushProvider(IOptions<AzureDnsOptions> options) => _options = options.Value;

    public string ProviderKey => "azure-dns";
    public bool IsConfigured =>
        !string.IsNullOrEmpty(_options.TenantId) && !string.IsNullOrEmpty(_options.ClientId) && !string.IsNullOrEmpty(_options.ClientSecret);

    public string BuildAuthorizationUrl(string state, string codeChallenge, string redirectUri)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId!,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["response_mode"] = "query",
            ["scope"] = $"{Scope} openid",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };
        return $"https://login.microsoftonline.com/{_options.TenantId}/oauth2/v2.0/authorize?" +
            string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    public async Task<DnsPushResult> ExchangeAndPushAsync(
        string code, string codeVerifier, string redirectUri, DnsRecordChange change, CancellationToken cancellationToken)
    {
        var confidentialClient = ConfidentialClientApplicationBuilder.Create(_options.ClientId)
            .WithClientSecret(_options.ClientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{_options.TenantId}")
            .Build();

        AuthenticationResult authResult;
        try
        {
            authResult = await confidentialClient
                .AcquireTokenByAuthorizationCode([Scope], code)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MsalException ex)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Azure rejected the authorization code exchange: {ex.Message}");
        }

        var armClient = new ArmClient(new FixedTokenCredential(authResult.AccessToken, authResult.ExpiresOn));

        var zoneName = ZoneNameFor(change.Name);
        var zone = await FindZoneAsync(armClient, zoneName, cancellationToken).ConfigureAwait(false);
        if (zone is null)
        {
            return new DnsPushResult(DnsPushOutcome.ZoneNotFound,
                $"Couldn't find {zoneName} in any subscription you authorized — check you have DNS Zone Contributor rights on it.");
        }

        return await PushRecordAsync(zone, zoneName, change, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DnsZoneResource?> FindZoneAsync(ArmClient armClient, string zoneName, CancellationToken cancellationToken)
    {
        await foreach (var subscription in armClient.GetSubscriptions().GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await foreach (var candidate in subscription.GetDnsZonesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(candidate.Data.Name, zoneName, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    private static async Task<DnsPushResult> PushRecordAsync(DnsZoneResource zone, string zoneName, DnsRecordChange change, CancellationToken cancellationToken)
    {
        // "mta-sts.contoso.co.uk" under zone "contoso.co.uk" -> relative record name "mta-sts".
        var relativeName = change.Name[..^(zoneName.Length + 1)];

        try
        {
            if (string.Equals(change.RecordType, "CNAME", StringComparison.OrdinalIgnoreCase))
            {
                var data = new DnsCnameRecordData { TtlInSeconds = 3600, Cname = new DnsCnameRecord { Cname = change.DesiredValue } };
                await zone.GetDnsCnameRecords().CreateOrUpdateAsync(WaitUntil.Completed, relativeName, data, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var data = new DnsTxtRecordData { TtlInSeconds = 3600 };
                data.DnsTxtRecords.Add(new DnsTxtRecordInfo { Values = { change.DesiredValue } });
                await zone.GetDnsTxtRecords().CreateOrUpdateAsync(WaitUntil.Completed, relativeName, data, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Azure.RequestFailedException ex)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Azure rejected the record push: {ex.Message}");
        }

        return new DnsPushResult(DnsPushOutcome.Pushed, null);
    }

    private static string ZoneNameFor(string recordName)
    {
        var firstDot = recordName.IndexOf('.');
        return firstDot < 0 ? recordName : recordName[(firstDot + 1)..];
    }

    /// <summary>Wraps an access token already obtained via the delegated authorization-code
    /// exchange above — ArmClient needs a TokenCredential, but there is nothing for it to actually
    /// fetch here; it already has the one token this whole operation is scoped to.</summary>
    private sealed class FixedTokenCredential : TokenCredential
    {
        private readonly string _accessToken;
        private readonly DateTimeOffset _expiresOn;

        public FixedTokenCredential(string accessToken, DateTimeOffset expiresOn)
        {
            _accessToken = accessToken;
            _expiresOn = expiresOn;
        }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(_accessToken, _expiresOn);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(new AccessToken(_accessToken, _expiresOn));
    }
}
```

- [ ] **Step 2: Build, and adjust SDK type/member names if `Azure.ResourceManager.Dns` differs**

Run: `dotnet build src/DotMarc/DotMarc.csproj`
Expected: succeeds once any `Azure.ResourceManager.Dns.Models` type/member names above are
corrected to match what 1.1.1 actually exposes (see the note above the implementation step).

- [ ] **Step 3: Commit**

```bash
git add src/DotMarc/DnsPush/AzureDnsOptions.cs src/DotMarc/DnsPush/AzureDnsPushProvider.cs
git commit -m "Add AzureDnsPushProvider"
```

---

## Task 10: Wire everything into Program.cs

**Files:**
- Modify: `src/DotMarc/Program.cs`

**Interfaces:**
- Consumes: every type from Tasks 2–9.
- Produces: `GET /dns-push/{provider}/start`, `GET /dns-push/{provider}/callback` — consumed by
  Task 11 and Task 12's UI buttons (`Navigation.NavigateTo($"/dns-push/{providerKey}/start?...",
  forceLoad: true)`).

No automated test — this is DI wiring plus two minimal-API endpoints exercising real OAuth/HTTP
that can't run in CI, same acceptance as the existing `/.well-known/mta-sts*` endpoints.

- [ ] **Step 1: Register the DNS push services**

In `src/DotMarc/Program.cs`, immediately after the existing MTA-STS `AddHttpClient`/`AddScoped`
block (after the `IMtaStsHostProvisioner` registration, i.e. after the line registering
`AzureMtaStsHostProvisioner`/`CaddyMtaStsHostProvisioner`), add:

```csharp
builder.Services.Configure<DotMarc.DnsPush.CloudflareDnsOptions>(builder.Configuration.GetSection(DotMarc.DnsPush.CloudflareDnsOptions.SectionName));
builder.Services.AddHttpClient<DotMarc.DnsPush.CloudflareDnsPushProvider>();
builder.Services.AddSingleton<DotMarc.DnsPush.IDnsPushProvider>(sp => sp.GetRequiredService<DotMarc.DnsPush.CloudflareDnsPushProvider>());

builder.Services.Configure<DotMarc.DnsPush.AzureDnsOptions>(builder.Configuration.GetSection(DotMarc.DnsPush.AzureDnsOptions.SectionName));
builder.Services.AddSingleton<DotMarc.DnsPush.AzureDnsPushProvider>();
builder.Services.AddSingleton<DotMarc.DnsPush.IDnsPushProvider>(sp => sp.GetRequiredService<DotMarc.DnsPush.AzureDnsPushProvider>());

builder.Services.AddHttpClient<DotMarc.DnsPush.IDnsProviderDetector, DotMarc.DnsPush.DnsProviderDetector>(client =>
{
    client.BaseAddress = new Uri("https://cloudflare-dns.com/");
    client.DefaultRequestHeaders.Add("Accept", "application/dns-json");
});

builder.Services.AddHttpClient<DotMarc.DnsPush.IDmarcTxtLookup, DotMarc.DnsPush.DmarcTxtLookup>(client =>
{
    client.BaseAddress = new Uri("https://cloudflare-dns.com/");
    client.DefaultRequestHeaders.Add("Accept", "application/dns-json");
});

builder.Services.AddSingleton<DotMarc.DnsPush.DnsPushStateProtector>();
```

- [ ] **Step 2: Add `using DotMarc.DnsPush;`**

At the top of `Program.cs`, alongside the existing `using DotMarc.MtaSts;`, add:

```csharp
using DotMarc.DnsPush;
```

- [ ] **Step 3: Add the two minimal-API endpoints**

Immediately after the existing `/.well-known/mta-sts.txt` endpoint block (right before
`app.MapRazorComponents<DotMarc.Components.App>()`), add:

```csharp
// Unlike the two /.well-known/mta-sts* endpoints above, these run under this app's own hostname
// and DO require the caller to already be signed in — a push is a write action gated by the same
// permission its target already needs (MtaStsManage for the CNAME, DomainsEdit for the DMARC TXT
// record), checked explicitly below since /start doesn't yet know which target it's for from route
// data alone.
app.MapGet("/dns-push/{provider}/start", async (
    string provider, int domainId, string target, HttpContext httpContext,
    IEnumerable<IDnsPushProvider> pushProviders, DnsPushStateProtector stateProtector,
    IAuthorizationService authorizationService) =>
{
    var requiredPolicy = target switch { "mta-sts" => "MtaStsManage", "dmarc" => "DomainsEdit", _ => null };
    if (requiredPolicy is null)
    {
        return Results.BadRequest();
    }

    var authResult = await authorizationService.AuthorizeAsync(httpContext.User, requiredPolicy);
    if (!authResult.Succeeded)
    {
        return Results.Forbid();
    }

    var pushProvider = pushProviders.SingleOrDefault(p => p.ProviderKey == provider && p.IsConfigured);
    if (pushProvider is null)
    {
        return Results.NotFound();
    }

    var (codeVerifier, codeChallenge) = PkceGenerator.Generate();
    var state = stateProtector.Protect(domainId, target, codeVerifier, DateTimeOffset.UtcNow);
    var redirectUri = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/dns-push/{provider}/callback";

    return Results.Redirect(pushProvider.BuildAuthorizationUrl(state, codeChallenge, redirectUri));
});

app.MapGet("/dns-push/{provider}/callback", async (
    string provider, string? code, string? state, string? error, HttpContext httpContext,
    IEnumerable<IDnsPushProvider> pushProviders, DnsPushStateProtector stateProtector,
    IDbContextFactory<DotMarcDbContext> dbContextFactory, IDmarcTxtLookup dmarcTxtLookup,
    IOptions<DotMarc.MtaSts.MtaStsOptions> mtaStsOptions, IOptions<GraphOptions> graphOptions) =>
{
    var pushProvider = pushProviders.SingleOrDefault(p => p.ProviderKey == provider && p.IsConfigured);
    var decodedState = state is null ? null : stateProtector.Unprotect(state, DateTimeOffset.UtcNow);
    if (pushProvider is null || decodedState is null)
    {
        return Results.Redirect("/dashboard?dnsPush=invalid");
    }

    await using var context = await dbContextFactory.CreateDbContextAsync();
    var domain = await context.Domains.AsNoTracking().SingleOrDefaultAsync(d => d.Id == decodedState.DomainId);
    if (domain is null)
    {
        return Results.Redirect("/dashboard?dnsPush=invalid");
    }

    var returnPath = decodedState.PushTarget == "mta-sts" ? "/mta-sts" : $"/domains/{domain.Name}";

    if (error is not null || code is null)
    {
        return Results.Redirect($"{returnPath}?dnsPush=cancelled");
    }

    DnsRecordChange change;
    if (decodedState.PushTarget == "mta-sts")
    {
        var hostingHostname = mtaStsOptions.Value.HostingHostname;
        if (string.IsNullOrEmpty(hostingHostname))
        {
            return Results.Redirect($"{returnPath}?dnsPush=error");
        }
        change = new DnsRecordChange(DnsRecordChangeKind.Create, "CNAME", $"mta-sts.{domain.Name}", hostingHostname, null);
    }
    else
    {
        var existing = await dmarcTxtLookup.LookupAsync(domain.Name, CancellationToken.None);
        var mailbox = graphOptions.Value.MailboxAddress;
        if (existing is null)
        {
            change = new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"_dmarc.{domain.Name}", $"v=DMARC1; p=none; rua=mailto:{mailbox}", null);
        }
        else
        {
            var merged = DmarcRuaMerge.TryMerge(existing, mailbox);
            if (merged is null)
            {
                return Results.Redirect($"{returnPath}?dnsPush=unmergeable");
            }
            change = new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"_dmarc.{domain.Name}", merged, existing);
        }
    }

    var redirectUri = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/dns-push/{provider}/callback";
    var result = await pushProvider.ExchangeAndPushAsync(code, decodedState.CodeVerifier, redirectUri, change, CancellationToken.None);

    var resultFlag = result.Outcome switch
    {
        DnsPushOutcome.Pushed => "pushed",
        DnsPushOutcome.ZoneNotFound => "zone-not-found",
        _ => "error"
    };
    return Results.Redirect($"{returnPath}?dnsPush={resultFlag}");
});
```

- [ ] **Step 4: Build**

Run: `dotnet build src/DotMarc/DotMarc.csproj`
Expected: succeeds.

- [ ] **Step 5: Run the full test suite to confirm no regression**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj`
Expected: PASS, all tests (295 + this plan's new ones so far).

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Program.cs
git commit -m "Wire DNS push providers and add the start/callback endpoints"
```

---

## Task 11: ManageMtaSts.razor — push button

**Files:**
- Modify: `src/DotMarc/Components/Pages/ManageMtaSts.razor`

**Interfaces:**
- Consumes: `IDnsProviderDetector.DetectAsync`, `IEnumerable<IDnsPushProvider>`.

No automated test — Razor UI in this codebase is verified manually (established precedent
throughout this project, e.g. the unsaved-changes guard, the MX-hosts sync button).

- [ ] **Step 1: Add the needed injections and using**

At the top of `ManageMtaSts.razor`, add alongside the existing `@using`/`@inject` lines:

```razor
@using DotMarc.DnsPush
@inject IDnsProviderDetector DnsProviderDetector
@inject IEnumerable<IDnsPushProvider> DnsPushProviders
@inject NavigationManager Navigation
```

- [ ] **Step 2: Add `Status` and `IsDetectingProvider` to `MtaStsRow`, and select `Status` in `LoadAsync`**

In the `MtaStsRow` class, add:

```csharp
public MtaStsStatus Status { get; set; }
public bool IsDetectingProvider { get; set; }
```

In `LoadAsync`'s `.Select(d => new MtaStsRow { ... })`, add:

```csharp
Status = d.MtaStsStatus,
```

- [ ] **Step 3: Add the push button to the row template's trailing action column**

In the last `<MudTd Style="width: 3rem;">` cell (the one with the Save icon button), add the push
button *before* the existing Save button, only rendered when the row is waiting on DNS:

```razor
<MudTd Style="width: 3rem;">
    @if (context.Status == MtaStsStatus.PendingDns)
    {
        @if (context.IsDetectingProvider)
        {
            <MudProgressCircular Size="Size.Small" Indeterminate="true" />
        }
        else
        {
            <MudIconButton Icon="@Icons.Material.Filled.CloudSync" Size="Size.Small"
                           title="Push the CNAME via your DNS provider" aria-label="@($"Push MTA-STS CNAME for {context.Name} via your DNS provider")"
                           OnClick="@(() => PushCnameAsync(context))" />
        }
    }
    <MudIconButton Icon="@Icons.Material.Filled.Save" Color="Color.Primary" Size="Size.Small"
                   title="Save" aria-label="@($"Save MTA-STS configuration for {context.Name}")"
                   OnClick="@(() => SaveAsync(context))" />
</MudTd>
```

- [ ] **Step 4: Add `PushCnameAsync` to the `@code` block**

```csharp
private async Task PushCnameAsync(MtaStsRow row)
{
    row.IsDetectingProvider = true;
    try
    {
        var detected = await DnsProviderDetector.DetectAsync(row.Name, CancellationToken.None);
        var providerKey = detected switch
        {
            DetectedDnsProvider.Cloudflare => "cloudflare",
            DetectedDnsProvider.AzureDns => "azure-dns",
            _ => null
        };
        var pushProvider = providerKey is null ? null : DnsPushProviders.SingleOrDefault(p => p.ProviderKey == providerKey && p.IsConfigured);
        if (pushProvider is null)
        {
            Snackbar.Add($"Couldn't find a configured DNS push option for {row.Name} — add the CNAME manually.", Severity.Warning);
            return;
        }

        Navigation.NavigateTo($"/dns-push/{pushProvider.ProviderKey}/start?domainId={row.Id}&target=mta-sts", forceLoad: true);
    }
    finally
    {
        row.IsDetectingProvider = false;
    }
}
```

- [ ] **Step 5: Show a result toast on return from the push flow**

In `OnInitializedAsync`, after the existing `await LoadAsync();` line, add:

```csharp
if (RendererInfo.IsInteractive)
{
    ShowDnsPushResultToast();
}
```

Add a new method:

```csharp
private void ShowDnsPushResultToast()
{
    var query = System.Web.HttpUtility.ParseQueryString(new Uri(Navigation.Uri).Query);
    switch (query["dnsPush"])
    {
        case "pushed":
            Snackbar.Add("DNS record pushed. It can take a few minutes to propagate.", Severity.Success);
            break;
        case "cancelled":
            break; // user backed out of the provider's consent screen — no need to say anything
        case "zone-not-found":
            Snackbar.Add("Couldn't find that domain in the account you authorized.", Severity.Warning);
            break;
        case "unmergeable":
            Snackbar.Add("Couldn't safely compute a fix for that record — it needs a manual look.", Severity.Warning);
            break;
        case "error":
            Snackbar.Add("The DNS push failed. Try again, or add the record manually.", Severity.Error);
            break;
    }
}
```

- [ ] **Step 6: Build**

Run: `dotnet build src/DotMarc/DotMarc.csproj`
Expected: succeeds.

- [ ] **Step 7: Manual check**

Run the app locally (`docker compose up`, per `getting-started.mdx`), navigate to Manage MTA-STS,
enable a domain, and confirm: with no `CloudflareDns`/`AzureDns` config set, no push button
appears; the Save flow still works as before.

- [ ] **Step 8: Commit**

```bash
git add src/DotMarc/Components/Pages/ManageMtaSts.razor
git commit -m "Add DNS push button to Manage MTA-STS"
```

---

## Task 12: DomainDetail.razor — push button and diff dialog

**Files:**
- Create: `src/DotMarc/Components/Dialogs/ConfirmDnsRecordPushDialog.razor`
- Modify: `src/DotMarc/Components/Pages/DomainDetail.razor`

**Interfaces:**
- Consumes: `IDnsProviderDetector`, `IEnumerable<IDnsPushProvider>`, `IDmarcTxtLookup`,
  `DmarcRuaMerge.TryMerge`, `IOptions<GraphOptions>`.

No automated test, same reasoning as Task 11.

- [ ] **Step 1: Create the diff dialog**

```razor
@* src/DotMarc/Components/Dialogs/ConfirmDnsRecordPushDialog.razor *@
<MudDialog>
    <TitleContent>Push DMARC record fix</TitleContent>
    <DialogContent>
        <MudText Typo="Typo.body2" Class="mb-2">Current value at <code>_dmarc.@DomainName</code>:</MudText>
        <MudText Typo="Typo.body2" Class="mb-4" Style="font-family: monospace; word-break: break-all;">@ExistingValue</MudText>
        <MudText Typo="Typo.body2" Class="mb-2">Will be replaced with:</MudText>
        <MudText Typo="Typo.body2" Style="font-family: monospace; word-break: break-all;">@ProposedValue</MudText>
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        <MudButton Color="Color.Primary" Variant="Variant.Filled" OnClick="Confirm">Apply</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public string DomainName { get; set; } = "";
    [Parameter] public string ExistingValue { get; set; } = "";
    [Parameter] public string ProposedValue { get; set; } = "";

    private void Confirm() => MudDialog.Close(DialogResult.Ok(true));
    private void Cancel() => MudDialog.Cancel();
}
```

- [ ] **Step 2: Add injections and using to DomainDetail.razor**

Alongside the existing `@using`/`@inject` lines:

```razor
@using DotMarc.DnsPush
@using DotMarc.Components.Dialogs
@inject IDnsProviderDetector DnsProviderDetector
@inject IEnumerable<IDnsPushProvider> DnsPushProviders
@inject IDmarcTxtLookup DmarcTxtLookup
@inject IOptions<GraphOptions> GraphOptions
@inject IDialogService DialogService
@inject ISnackbar Snackbar
```

(`IDialogService` and `ISnackbar` may already be present from earlier work this session — check
before adding a duplicate `@inject` line.)

- [ ] **Step 3: Add the push button next to `DmarcCheckDetail`**

In the Overview tab, immediately after the existing block that renders `_domain.DmarcCheckDetail`
(the `<MudText Typo="Typo.body2" Class="mt-1">@_domain.DmarcCheckDetail</MudText>` line), add:

```razor
@if (_domain.DmarcCheckStatus is DmarcCheckStatus.MissingOwnRecord or DmarcCheckStatus.Misconfigured)
{
    <AuthorizeView Policy="DomainsEdit" Context="dmarcPushAuthState">
        <div class="mt-2">
            @if (_isPushingDmarcRecord)
            {
                <MudProgressCircular Size="Size.Small" Indeterminate="true" />
            }
            else
            {
                <MudButton Variant="Variant.Text" Color="Color.Primary" StartIcon="@Icons.Material.Filled.CloudSync"
                           OnClick="PushDmarcRecordAsync">Push via your DNS provider</MudButton>
            }
        </div>
    </AuthorizeView>
}
```

- [ ] **Step 4: Add `_isPushingDmarcRecord` and `PushDmarcRecordAsync` to the `@code` block**

```csharp
private bool _isPushingDmarcRecord;

private async Task PushDmarcRecordAsync()
{
    _isPushingDmarcRecord = true;
    try
    {
        var detected = await DnsProviderDetector.DetectAsync(DomainName, CancellationToken.None);
        var providerKey = detected switch
        {
            DetectedDnsProvider.Cloudflare => "cloudflare",
            DetectedDnsProvider.AzureDns => "azure-dns",
            _ => null
        };
        var pushProvider = providerKey is null ? null : DnsPushProviders.SingleOrDefault(p => p.ProviderKey == providerKey && p.IsConfigured);
        if (pushProvider is null)
        {
            Snackbar.Add("Couldn't find a configured DNS push option for this domain — add the record manually.", Severity.Warning);
            return;
        }

        if (_domain!.DmarcCheckStatus != DmarcCheckStatus.Misconfigured)
        {
            Navigation.NavigateTo($"/dns-push/{pushProvider.ProviderKey}/start?domainId={_domain.Id}&target=dmarc", forceLoad: true);
            return;
        }

        var existing = await DmarcTxtLookup.LookupAsync(DomainName, CancellationToken.None);
        var merged = existing is null ? null : DmarcRuaMerge.TryMerge(existing, GraphOptions.Value.MailboxAddress);
        if (existing is null || merged is null)
        {
            Snackbar.Add("Couldn't safely compute a fix for this record — it needs a manual look.", Severity.Warning);
            return;
        }

        var parameters = new DialogParameters<ConfirmDnsRecordPushDialog>
        {
            { x => x.DomainName, DomainName },
            { x => x.ExistingValue, existing },
            { x => x.ProposedValue, merged }
        };
        var dialogRef = await DialogService.ShowAsync<ConfirmDnsRecordPushDialog>("Push DMARC record fix", parameters);
        var result = await dialogRef.Result;
        if (result is { Canceled: false })
        {
            Navigation.NavigateTo($"/dns-push/{pushProvider.ProviderKey}/start?domainId={_domain.Id}&target=dmarc", forceLoad: true);
        }
    }
    finally
    {
        _isPushingDmarcRecord = false;
    }
}
```

- [ ] **Step 5: Show a result toast on return, mirroring Task 11's pattern**

In `OnInitializedAsync`, after the existing `RendererInfo.IsInteractive` gated enrichment block,
add the same `ShowDnsPushResultToast()` call and private method as Task 11 Step 5 (identical
switch on `dnsPush` query values).

- [ ] **Step 6: Build**

Run: `dotnet build src/DotMarc/DotMarc.csproj`
Expected: succeeds.

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj`
Expected: PASS, no regressions.

- [ ] **Step 8: Manual check**

Load a domain detail page for a domain with `DmarcCheckStatus.MissingOwnRecord` or `.Misconfigured`
in the demo dataset (`cobalt-freight.example`'s siblings, or seed one) and confirm the button
renders only under `DomainsEdit`, and that `.Misconfigured` opens the diff dialog rather than
navigating straight away.

- [ ] **Step 9: Commit**

```bash
git add src/DotMarc/Components/Dialogs/ConfirmDnsRecordPushDialog.razor src/DotMarc/Components/Pages/DomainDetail.razor
git commit -m "Add DNS push button and diff-confirm dialog to the domain detail page"
```

---

## Task 13: Docs

**Files:**
- Modify: `website/docs/getting-started.mdx`
- Modify: `website/docs/mta-sts.mdx`

No test — documentation only.

- [ ] **Step 1: Add a new getting-started.mdx section**

After the existing `### MTA-STS policy hosting (optional)` subsection (under `## Run`), add:

```markdown
### DNS provider push (optional)

If a domain's DNS is hosted on Cloudflare or Azure DNS, dotMARC can push the MTA-STS CNAME or the
DMARC TXT record straight there instead of you copying it in by hand — authenticated fresh each
time through that provider's own consent screen, nothing stored.

**Cloudflare**: register a self-managed OAuth client (**Manage account** → **OAuth clients** in the
Cloudflare dashboard), scoped to `Zone.DNS` edit, with a redirect URI of
`https://<your-deployment-host>/dns-push/cloudflare/callback`. Set:

| Variable | Description |
| --- | --- |
| `CloudflareDns__ClientId` | The OAuth client's ID |
| `CloudflareDns__ClientSecret` | The OAuth client's secret |

**Azure DNS**: register a *third*, separate Entra app registration (do not reuse the mailbox or
dashboard app) — **App registrations** → **New registration**, then **Authentication** → add a
**Web** redirect URI of `https://<your-deployment-host>/dns-push/azure-dns/callback`, then **API
permissions** → add the delegated **Azure Service Management** → `user_impersonation` permission.
Set:

| Variable | Description |
| --- | --- |
| `AzureDns__TenantId` | Your tenant ID |
| `AzureDns__ClientId` | This app registration's client ID |
| `AzureDns__ClientSecret` | This app registration's client secret |

Both are independently optional — leave either unset and that provider's push button simply never
appears, with no other effect on the app.
```

- [ ] **Step 2: Add a line to mta-sts.mdx**

In the "Enabling a domain" section, after the paragraph describing the CNAME requirement, add:

```markdown
If this deployment has DNS provider push configured (see [Getting
Started](./getting-started.mdx#dns-provider-push-optional)) and the domain's DNS is hosted on
Cloudflare or Azure DNS, a push button appears next to the CNAME instructions instead — it pushes
the record for you via that provider's own consent screen, with nothing stored.
```

- [ ] **Step 3: Commit**

```bash
git add website/docs/getting-started.mdx website/docs/mta-sts.mdx
git commit -m "Document DNS provider push setup"
```
