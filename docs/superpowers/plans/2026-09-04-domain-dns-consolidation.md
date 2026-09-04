# Domain DNS Management Consolidation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Consolidate DMARC/TLSRPT/MTA-STS status and push controls onto each domain's own detail page, and generalize the existing-record confirm flow (today only DMARC has it, and only when cached status already flags a problem) to all three push targets with a live pre-push check — including detection of third-party CNAME delegation.

**Architecture:** A small, pure `DnsRecordPushDecision.NeedsConfirmation` helper decides whether a live-checked existing value warrants a confirm dialog before any of the three push handlers proceed. `DmarcTxtLookup`/`TlsrptTxtLookup` gain CNAME-delegation detection via a new `DnsRecordLookupResult` return type; a new `MtaStsCnameLookup` mirrors their DNS-over-HTTPS pattern for MTA-STS. `ConfirmDnsRecordPushDialog` becomes record-type-agnostic. A new `DomainMtaStsPanel` component replaces the standalone Manage MTA-STS page, hosted in `DomainDetail.razor`'s MTA-STS tab and reusing that page's existing popup-push plumbing via a passed-down delegate. `Program.cs`'s MTA-STS callback branch gains the same create-vs-merge decision DMARC/TLSRPT already have.

**Tech Stack:** .NET 10, Blazor Server, EF Core/Npgsql, MudBlazor, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-04-domain-dns-consolidation-design.md`

## Global Constraints

- No database schema changes.
- No change to the OAuth/popup push mechanism itself (fixed and confirmed working earlier today) — only what happens before a push is initiated, and where the controls live.
- `DnsRecordLookupResult`'s `DelegatedToCname` applies to DMARC/TLSRPT only — MTA-STS's own lookup has no delegation concept (a CNAME at `mta-sts.<domain>` is its own normal, expected record type).
- No test scaffolding (HTTP-call mocking) for any of the DNS-over-HTTPS lookup classes (`DmarcTxtLookup`, `TlsrptTxtLookup`, `MtaStsCnameLookup`) — matches this codebase's existing, established convention for this exact class of code. Pure parsing/decision logic with no network dependency (the CNAME-detection parsing, the shared confirmation-decision helper) is testable and gets unit tests.
- No Razor/Blazor component test harness exists in this repo — UI changes are verified manually, per this project's established convention.

---

### Task 1: CNAME-delegation detection in the DMARC/TLSRPT lookups

**Files:**
- Modify: `src/DotMarc/DnsPush/IDmarcTxtLookup.cs`
- Modify: `src/DotMarc/DnsPush/DmarcTxtLookup.cs`
- Modify: `src/DotMarc/DnsPush/ITlsrptTxtLookup.cs`
- Modify: `src/DotMarc/DnsPush/TlsrptTxtLookup.cs`
- Modify: `src/DotMarc/Program.cs`
- Create: `test/DotMarc.Tests/DnsPush/DnsRecordLookupResultParsingTests.cs`

**Interfaces:**
- Produces: `DnsRecordLookupResult(string? DirectValue, string? DelegatedToCname)` record, and both lookup interfaces returning `Task<DnsRecordLookupResult>` instead of `Task<string?>`. Consumed by Task 4 (DomainDetail.razor push handlers) and this task's own `Program.cs` update.

- [ ] **Step 1: Write the new record type and update both interfaces**

Add a new file `src/DotMarc/DnsPush/DnsRecordLookupResult.cs`:

```csharp
namespace DotMarc.DnsPush;

/// <summary>The result of looking up a record's current live DNS state. DirectValue is the final
/// resolved value (same as today's plain string result) — non-null whenever something resolves,
/// regardless of whether a CNAME hop happened first. DelegatedToCname is set only when the record
/// at the expected name is itself a CNAME (not a direct TXT record) — e.g. a domain's _dmarc TXT
/// delegated to a third-party DMARC monitoring service via CNAME. A plain TXT query transparently
/// follows CNAMEs and would otherwise lose this distinction.</summary>
public sealed record DnsRecordLookupResult(string? DirectValue, string? DelegatedToCname);
```

Open `src/DotMarc/DnsPush/IDmarcTxtLookup.cs`. Replace its content:

```csharp
namespace DotMarc.DnsPush;

public interface IDmarcTxtLookup
{
    Task<DnsRecordLookupResult> LookupAsync(string domainName, CancellationToken cancellationToken);
}
```

Open `src/DotMarc/DnsPush/ITlsrptTxtLookup.cs`. Replace its content:

```csharp
namespace DotMarc.DnsPush;

public interface ITlsrptTxtLookup
{
    Task<DnsRecordLookupResult> LookupAsync(string domainName, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Update DmarcTxtLookup to detect the CNAME hop**

Open `src/DotMarc/DnsPush/DmarcTxtLookup.cs`. Replace the whole file:

```csharp
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

    public async Task<DnsRecordLookupResult> LookupAsync(string domainName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString($"_dmarc.{domainName}")}&type=TXT");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;
        return DnsRecordLookupParsing.ParseTxtWithCnameDetection(parsed.Answer);
    }

    private sealed record DnsOverHttpsResponse(
        [property: JsonPropertyName("Status")] int Status,
        [property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer(
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("data")] string Data);
}
```

- [ ] **Step 3: Update TlsrptTxtLookup the same way**

Open `src/DotMarc/DnsPush/TlsrptTxtLookup.cs`. Replace the whole file:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.DnsPush;

public sealed class TlsrptTxtLookup : ITlsrptTxtLookup
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public TlsrptTxtLookup(HttpClient http) => _http = http;

    public async Task<DnsRecordLookupResult> LookupAsync(string domainName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString($"_smtp._tls.{domainName}")}&type=TXT");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;
        return DnsRecordLookupParsing.ParseTxtWithCnameDetection(parsed.Answer);
    }

    private sealed record DnsOverHttpsResponse([property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
```

Note: `DmarcTxtLookup`'s and `TlsrptTxtLookup`'s private `DnsAnswer`/`DnsOverHttpsResponse` records stay separate per file (each file's `Answer` list is `List<DnsAnswer>` using that file's own private nested type) — the shared parsing helper below is generic over a minimal shape both can produce.

- [ ] **Step 4: Write the shared parsing helper**

Create `src/DotMarc/DnsPush/DnsRecordLookupParsing.cs`:

```csharp
namespace DotMarc.DnsPush;

/// <summary>Shared answer-chain parsing for DmarcTxtLookup/TlsrptTxtLookup: both query type=TXT
/// against a name that might actually be a CNAME to somewhere else (e.g. a domain's _dmarc record
/// delegated to a third-party DMARC monitoring service). A plain TXT query transparently follows
/// CNAMEs, so the raw DNS-over-HTTPS answer array is the only place that hop is still visible —
/// type 5 is CNAME, type 16 is TXT, per standard DNS RR type numbers.</summary>
public static class DnsRecordLookupParsing
{
    public static DnsRecordLookupResult ParseTxtWithCnameDetection(IEnumerable<(int Type, string Data)>? answers)
    {
        if (answers is null)
        {
            return new DnsRecordLookupResult(null, null);
        }

        var list = answers.ToList();
        var cname = list.FirstOrDefault(a => a.Type == 5).Data;
        var txt = list.FirstOrDefault(a => a.Type == 16);
        var directValue = txt.Data is null ? null : string.Join("", txt.Data.Split("\" \"")).Trim('"');
        return new DnsRecordLookupResult(directValue, cname);
    }
}
```

Now go back and fix the two call sites in Step 2 and Step 3 — `parsed.Answer` is a `List<DnsAnswer>?` where `DnsAnswer` is each file's own private record with `Type`/`Data` properties, not the tuple shape `ParseTxtWithCnameDetection` takes. Change both call sites from:

```csharp
return DnsRecordLookupParsing.ParseTxtWithCnameDetection(parsed.Answer);
```

to:

```csharp
return DnsRecordLookupParsing.ParseTxtWithCnameDetection(parsed.Answer?.Select(a => (a.Type, a.Data)));
```

in both `DmarcTxtLookup.cs` and `TlsrptTxtLookup.cs`.

- [ ] **Step 5: Write the parsing unit tests**

Create `test/DotMarc.Tests/DnsPush/DnsRecordLookupResultParsingTests.cs`:

```csharp
using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DnsRecordLookupResultParsingTests
{
    [Fact]
    public void ParseTxtWithCnameDetection_ReturnsBothNull_WhenNoAnswers()
    {
        var result = DnsRecordLookupParsing.ParseTxtWithCnameDetection(null);

        Assert.Null(result.DirectValue);
        Assert.Null(result.DelegatedToCname);
    }

    [Fact]
    public void ParseTxtWithCnameDetection_ReturnsDirectValue_WhenPlainTxtRecord()
    {
        var answers = new[] { (Type: 16, Data: "\"v=DMARC1; p=reject;\"") };

        var result = DnsRecordLookupParsing.ParseTxtWithCnameDetection(answers);

        Assert.Equal("v=DMARC1; p=reject;", result.DirectValue);
        Assert.Null(result.DelegatedToCname);
    }

    [Fact]
    public void ParseTxtWithCnameDetection_DetectsDelegation_WhenCnameHopPrecedesTxt()
    {
        var answers = new[]
        {
            (Type: 5, Data: "_dmarc.example_com._d.easydmarc.pro."),
            (Type: 16, Data: "\"v=DMARC1;p=reject;\"")
        };

        var result = DnsRecordLookupParsing.ParseTxtWithCnameDetection(answers);

        Assert.Equal("v=DMARC1;p=reject;", result.DirectValue);
        Assert.Equal("_dmarc.example_com._d.easydmarc.pro.", result.DelegatedToCname);
    }

    [Fact]
    public void ParseTxtWithCnameDetection_JoinsSplitTxtStrings()
    {
        var answers = new[] { (Type: 16, Data: "\"v=DMARC1; \" \"p=reject;\"") };

        var result = DnsRecordLookupParsing.ParseTxtWithCnameDetection(answers);

        Assert.Equal("v=DMARC1; p=reject;", result.DirectValue);
    }
}
```

- [ ] **Step 6: Update Program.cs's call sites**

Open `src/DotMarc/Program.cs`. Find the `dmarc` branch inside `/dns-push/{provider}/callback`:

```csharp
    else if (decodedState.PushTarget == "dmarc")
    {
        var existing = await dmarcTxtLookup.LookupAsync(domain.Name, CancellationToken.None);
        var mailbox = graphOptions.Value.MailboxAddress;
        if (existing is null)
        {
            changes = [new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"_dmarc.{domain.Name}", $"v=DMARC1; p=none; rua=mailto:{mailbox}", null, domain.Name)];
        }
        else
        {
            var merged = DmarcRuaMerge.TryMerge(existing, mailbox);
            if (merged is null)
            {
                return DnsPushPopupResult.Close("unmergeable");
            }
            changes = [new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"_dmarc.{domain.Name}", merged, existing, domain.Name)];
        }
    }
```

Replace it with:

```csharp
    else if (decodedState.PushTarget == "dmarc")
    {
        var existing = await dmarcTxtLookup.LookupAsync(domain.Name, CancellationToken.None);
        var mailbox = graphOptions.Value.MailboxAddress;
        if (existing.DirectValue is null)
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
        }
    }
```

Find the `tlsrpt` branch (the final `else` in the same if/else-if/else chain):

```csharp
    else
    {
        var mailbox = graphOptions.Value.TlsrptMailboxAddress;
        if (string.IsNullOrWhiteSpace(mailbox))
        {
            return DnsPushPopupResult.Close("error");
        }

        var existing = await tlsrptTxtLookup.LookupAsync(domain.Name, CancellationToken.None);
        if (existing is null)
        {
            changes = [new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"_smtp._tls.{domain.Name}", $"v=TLSRPTv1; rua=mailto:{mailbox}", null, domain.Name)];
        }
        else
        {
            var merged = TlsrptRuaMerge.TryMerge(existing, mailbox);
            if (merged is null)
            {
                return DnsPushPopupResult.Close("unmergeable");
            }
            changes = [new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"_smtp._tls.{domain.Name}", merged, existing, domain.Name)];
        }
    }
```

Replace it with:

```csharp
    else
    {
        var mailbox = graphOptions.Value.TlsrptMailboxAddress;
        if (string.IsNullOrWhiteSpace(mailbox))
        {
            return DnsPushPopupResult.Close("error");
        }

        var existing = await tlsrptTxtLookup.LookupAsync(domain.Name, CancellationToken.None);
        if (existing.DirectValue is null)
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
        }
    }
```

- [ ] **Step 7: Build and run the new tests**

Run: `dotnet build DotMarc.sln && dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "FullyQualifiedName~DnsRecordLookupResultParsingTests"`
Expected: Build succeeds (this task's signature change also breaks `DomainDetail.razor`'s two call sites — that's expected and out of scope for this task; Task 4 fixes them. `Program.cs` and the two lookup files are the only places this task touches that call `.LookupAsync`, so `dotnet build DotMarc.sln` will fail on `DomainDetail.razor` until Task 4 lands — **run `dotnet build src/DotMarc/DotMarc.csproj` scoped to just this project is not possible since DomainDetail.razor is in the same project; instead confirm only that `DnsRecordLookupParsing`, `DnsRecordLookupResult`, both lookup classes, and `Program.cs` compile correctly by reading them carefully — the full-solution build will go green once Task 4 lands, not before**). The 4 new tests pass, 4/4.

- [ ] **Step 8: Commit**

```bash
git add src/DotMarc/DnsPush/DnsRecordLookupResult.cs src/DotMarc/DnsPush/DnsRecordLookupParsing.cs src/DotMarc/DnsPush/IDmarcTxtLookup.cs src/DotMarc/DnsPush/DmarcTxtLookup.cs src/DotMarc/DnsPush/ITlsrptTxtLookup.cs src/DotMarc/DnsPush/TlsrptTxtLookup.cs src/DotMarc/Program.cs test/DotMarc.Tests/DnsPush/DnsRecordLookupResultParsingTests.cs
git commit -m "Detect third-party CNAME delegation in DMARC/TLSRPT lookups"
```

---

### Task 2: MTA-STS CNAME lookup

**Files:**
- Create: `src/DotMarc/MtaSts/IMtaStsCnameLookup.cs`
- Create: `src/DotMarc/MtaSts/MtaStsCnameLookup.cs`
- Modify: `src/DotMarc/Program.cs`

**Interfaces:**
- Produces: `IMtaStsCnameLookup.LookupAsync(string domainName, CancellationToken) : Task<string?>` — the current live CNAME target at `mta-sts.<domain>`, or null. Consumed by Task 5 (`DomainMtaStsPanel`) and Task 6 (`Program.cs`'s merge path).

- [ ] **Step 1: Write the interface**

Create `src/DotMarc/MtaSts/IMtaStsCnameLookup.cs`:

```csharp
namespace DotMarc.MtaSts;

public interface IMtaStsCnameLookup
{
    Task<string?> LookupAsync(string domainName, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Write the implementation**

Create `src/DotMarc/MtaSts/MtaStsCnameLookup.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.MtaSts;

/// <summary>Fetches the raw, currently-live mta-sts.&lt;domain&gt; CNAME target — used by the
/// MTA-STS push flow to decide Create vs. Merge before pushing, the same way
/// DmarcTxtLookup/TlsrptTxtLookup already do for their record types. A CNAME here is MTA-STS's own
/// normal, expected record type (unlike DMARC/TLSRPT, where finding one instead of a plain TXT
/// record means third-party delegation) — there is no delegation concept for this lookup.</summary>
public sealed class MtaStsCnameLookup : IMtaStsCnameLookup
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public MtaStsCnameLookup(HttpClient http) => _http = http;

    public async Task<string?> LookupAsync(string domainName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString($"mta-sts.{domainName}")}&type=CNAME");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;
        var answer = parsed.Answer?.FirstOrDefault(a => a.Type == 5);
        return answer?.Data.TrimEnd('.');
    }

    private sealed record DnsOverHttpsResponse([property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
```

- [ ] **Step 3: Register it for DI**

Open `src/DotMarc/Program.cs`. Find the existing DMARC/TLSRPT lookup registrations (search for `AddHttpClient<DotMarc.DnsPush.IDmarcTxtLookup`). Add a matching registration for the new lookup directly after them, following the exact same `client.BaseAddress` pattern:

```csharp
builder.Services.AddHttpClient<DotMarc.MtaSts.IMtaStsCnameLookup, DotMarc.MtaSts.MtaStsCnameLookup>(client =>
{
    client.BaseAddress = new Uri("https://cloudflare-dns.com/");
    client.DefaultRequestHeaders.Add("Accept", "application/dns-json");
});
```

- [ ] **Step 4: Build**

Run: `dotnet build DotMarc.sln`
Expected: Build succeeds (this task adds new, unreferenced-so-far code plus one DI registration — no existing call sites change).

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/MtaSts/IMtaStsCnameLookup.cs src/DotMarc/MtaSts/MtaStsCnameLookup.cs src/DotMarc/Program.cs
git commit -m "Add MTA-STS CNAME lookup for the live pre-push check"
```

---

### Task 3: Shared push-confirmation decision helper + generalized confirm dialog

**Files:**
- Create: `src/DotMarc/DnsPush/DnsRecordPushDecision.cs`
- Create: `test/DotMarc.Tests/DnsPush/DnsRecordPushDecisionTests.cs`
- Modify: `src/DotMarc/Components/Dialogs/ConfirmDnsRecordPushDialog.razor`

**Interfaces:**
- Produces: `DnsRecordPushDecision.NeedsConfirmation(string? existingValue, string? delegatedToCname, string proposedValue) : bool`. Consumed by Task 4 and Task 5.
- Produces: `ConfirmDnsRecordPushDialog`'s new parameter set — `RecordDescription`, `RecordName`, `ExistingValue`, `ProposedValue`, `DelegatedToCname` (all `string`, `DelegatedToCname` nullable). Consumed by Task 4 and Task 5.

- [ ] **Step 1: Write the decision helper**

Create `src/DotMarc/DnsPush/DnsRecordPushDecision.cs`:

```csharp
namespace DotMarc.DnsPush;

/// <summary>Shared "does this push need a confirm dialog first" decision, used identically by the
/// MTA-STS, DMARC, and TLSRPT push handlers. Each caller computes its own existingValue/
/// proposedValue first (the merge logic differs per record type — DmarcRuaMerge, TlsrptRuaMerge,
/// or MTA-STS's plain hosting-hostname target); this is only the generic "should I ask first"
/// step.</summary>
public static class DnsRecordPushDecision
{
    /// <summary>True when there's something to warn about before pushing: an existing value that
    /// differs from what's about to be pushed, or a third-party CNAME delegation (which always
    /// warrants a warning regardless of value comparison, since it's a different kind of record
    /// entirely, not just a different value of the same kind).</summary>
    public static bool NeedsConfirmation(string? existingValue, string? delegatedToCname, string proposedValue) =>
        delegatedToCname is not null || (existingValue is not null && !string.Equals(existingValue, proposedValue, StringComparison.Ordinal));
}
```

- [ ] **Step 2: Write its unit tests**

Create `test/DotMarc.Tests/DnsPush/DnsRecordPushDecisionTests.cs`:

```csharp
using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DnsRecordPushDecisionTests
{
    [Fact]
    public void NeedsConfirmation_ReturnsFalse_WhenNothingExistsYet()
    {
        var result = DnsRecordPushDecision.NeedsConfirmation(existingValue: null, delegatedToCname: null, proposedValue: "new-value");

        Assert.False(result);
    }

    [Fact]
    public void NeedsConfirmation_ReturnsFalse_WhenExistingAlreadyMatchesProposed()
    {
        var result = DnsRecordPushDecision.NeedsConfirmation(existingValue: "same-value", delegatedToCname: null, proposedValue: "same-value");

        Assert.False(result);
    }

    [Fact]
    public void NeedsConfirmation_ReturnsTrue_WhenExistingDiffersFromProposed()
    {
        var result = DnsRecordPushDecision.NeedsConfirmation(existingValue: "old-value", delegatedToCname: null, proposedValue: "new-value");

        Assert.True(result);
    }

    [Fact]
    public void NeedsConfirmation_ReturnsTrue_WhenDelegatedToCname_EvenIfNoDirectValue()
    {
        var result = DnsRecordPushDecision.NeedsConfirmation(existingValue: null, delegatedToCname: "target.example.com.", proposedValue: "new-value");

        Assert.True(result);
    }

    [Fact]
    public void NeedsConfirmation_ReturnsTrue_WhenDelegatedToCname_RegardlessOfValueMatch()
    {
        var result = DnsRecordPushDecision.NeedsConfirmation(existingValue: "new-value", delegatedToCname: "target.example.com.", proposedValue: "new-value");

        Assert.True(result);
    }
}
```

- [ ] **Step 3: Generalize the confirm dialog**

Open `src/DotMarc/Components/Dialogs/ConfirmDnsRecordPushDialog.razor`. Replace the whole file:

```razor
@* src/DotMarc/Components/Dialogs/ConfirmDnsRecordPushDialog.razor *@
<MudDialog>
    <TitleContent>Push @RecordDescription</TitleContent>
    <DialogContent>
        @if (DelegatedToCname is not null)
        {
            <MudText Typo="Typo.body2" Class="mb-2">
                <code>@RecordName</code> is currently a CNAME delegated to <code>@DelegatedToCname</code> —
                likely a third-party service managing this record. Proceeding replaces that CNAME and
                removes the delegation.
            </MudText>
        }
        else
        {
            <MudText Typo="Typo.body2" Class="mb-2">Current value at <code>@RecordName</code>:</MudText>
            <MudText Typo="Typo.body2" Class="mb-4" Style="font-family: monospace; word-break: break-all;">@ExistingValue</MudText>
        }
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
    [Parameter] public string RecordDescription { get; set; } = "";
    [Parameter] public string RecordName { get; set; } = "";
    [Parameter] public string ExistingValue { get; set; } = "";
    [Parameter] public string ProposedValue { get; set; } = "";
    [Parameter] public string? DelegatedToCname { get; set; }

    private void Confirm() => MudDialog.Close(DialogResult.Ok(true));
    private void Cancel() => MudDialog.Cancel();
}
```

- [ ] **Step 4: Build and run the new tests**

Run: `dotnet build src/DotMarc/DotMarc.csproj` — expect this to fail, because `DomainDetail.razor`'s existing call site still constructs `ConfirmDnsRecordPushDialog`'s old parameter names (`DomainName`). That call site is fixed in Task 4; this step is only to confirm the dialog file itself and the new helper compile with no syntax errors — read both files back over carefully instead of relying on a green full build here.

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "FullyQualifiedName~DnsRecordPushDecisionTests"`
Expected: 5/5 passing (this test project doesn't reference the Razor dialog, so it builds and runs independently of the `DomainDetail.razor` compile error above).

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/DnsPush/DnsRecordPushDecision.cs test/DotMarc.Tests/DnsPush/DnsRecordPushDecisionTests.cs src/DotMarc/Components/Dialogs/ConfirmDnsRecordPushDialog.razor
git commit -m "Generalize the DNS record push confirm dialog to all record types"
```

---

### Task 4: DomainDetail.razor — live-check before every DMARC/TLSRPT push

**Files:**
- Modify: `src/DotMarc/Components/Pages/DomainDetail.razor`

**Interfaces:**
- Consumes: `DnsRecordLookupResult` (Task 1), `IDmarcTxtLookup`/`ITlsrptTxtLookup`'s new return type (Task 1), `DnsRecordPushDecision.NeedsConfirmation` (Task 3), `ConfirmDnsRecordPushDialog`'s new parameters (Task 3).
- Produces: `OpenDnsPushPopupAsync(string providerKey, int domainId, string target)` stays exactly as it is today (unchanged signature) — Task 5's `DomainMtaStsPanel` will be handed a reference to this exact method as a parameter.

- [ ] **Step 1: Rewrite PushDmarcRecordAsync to always live-check**

Open `src/DotMarc/Components/Pages/DomainDetail.razor`. Find the `PushDmarcRecordAsync` method (currently checks `_domain!.DmarcCheckStatus != DmarcCheckStatus.Misconfigured` to decide whether to look anything up at all). Replace the whole method:

```csharp
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
            var pushProvider = await DnsPushProviders.FindConfiguredAsync(providerKey);
            if (pushProvider is null)
            {
                Snackbar.Add("Couldn't find a configured DNS push option for this domain — add the record manually.", Severity.Warning);
                return;
            }

            DnsRecordLookupResult existing;
            try
            {
                existing = await DmarcTxtLookup.LookupAsync(DomainName, CancellationToken.None);
            }
            catch (Exception)
            {
                Snackbar.Add($"Failed to look up {DomainName}'s current DMARC record. Try again.", Severity.Error);
                return;
            }

            string proposed;
            if (existing.DirectValue is null)
            {
                proposed = $"v=DMARC1; p=none; rua=mailto:{GraphOptions.Value.MailboxAddress}";
            }
            else
            {
                var merged = DmarcRuaMerge.TryMerge(existing.DirectValue, GraphOptions.Value.MailboxAddress);
                if (merged is null)
                {
                    Snackbar.Add("Couldn't safely compute a fix for this record — it needs a manual look.", Severity.Warning);
                    return;
                }
                proposed = merged;
            }

            if (DnsRecordPushDecision.NeedsConfirmation(existing.DirectValue, existing.DelegatedToCname, proposed))
            {
                var parameters = new DialogParameters<ConfirmDnsRecordPushDialog>
                {
                    { x => x.RecordDescription, "DMARC record" },
                    { x => x.RecordName, $"_dmarc.{DomainName}" },
                    { x => x.ExistingValue, existing.DirectValue ?? "" },
                    { x => x.ProposedValue, proposed },
                    { x => x.DelegatedToCname, existing.DelegatedToCname }
                };
                var dialogRef = await DialogService.ShowAsync<ConfirmDnsRecordPushDialog>("Push DMARC record", parameters);
                var result = await dialogRef.Result;
                if (result is null || result.Canceled)
                {
                    return;
                }
            }

            await OpenDnsPushPopupAsync(pushProvider.ProviderKey, _domain!.Id, "dmarc");
        }
        finally
        {
            _isPushingDmarcRecord = false;
        }
    }
```

- [ ] **Step 2: Rewrite PushTlsrptRecordAsync to match**

Find the `PushTlsrptRecordAsync` method. Replace the whole method:

```csharp
    private async Task PushTlsrptRecordAsync()
    {
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
            Snackbar.Add("Couldn't find a configured DNS push option for this domain — add the record manually.", Severity.Warning);
            return;
        }

        DnsRecordLookupResult existing;
        try
        {
            existing = await TlsrptTxtLookup.LookupAsync(DomainName, CancellationToken.None);
        }
        catch (Exception)
        {
            Snackbar.Add($"Failed to look up {DomainName}'s current TLS reporting record. Try again.", Severity.Error);
            return;
        }

        var mailbox = GraphOptions.Value.TlsrptMailboxAddress;
        string proposed;
        if (existing.DirectValue is null)
        {
            proposed = $"v=TLSRPTv1; rua=mailto:{mailbox}";
        }
        else
        {
            var merged = TlsrptRuaMerge.TryMerge(existing.DirectValue, mailbox);
            if (merged is null)
            {
                Snackbar.Add("Couldn't safely compute a fix for this record — it needs a manual look.", Severity.Warning);
                return;
            }
            proposed = merged;
        }

        if (DnsRecordPushDecision.NeedsConfirmation(existing.DirectValue, existing.DelegatedToCname, proposed))
        {
            var parameters = new DialogParameters<ConfirmDnsRecordPushDialog>
            {
                { x => x.RecordDescription, "TLS reporting record" },
                { x => x.RecordName, $"_smtp._tls.{DomainName}" },
                { x => x.ExistingValue, existing.DirectValue ?? "" },
                { x => x.ProposedValue, proposed },
                { x => x.DelegatedToCname, existing.DelegatedToCname }
            };
            var dialogRef = await DialogService.ShowAsync<ConfirmDnsRecordPushDialog>("Push TLS reporting record", parameters);
            var result = await dialogRef.Result;
            if (result is null || result.Canceled)
            {
                return;
            }
        }

        await OpenDnsPushPopupAsync(pushProvider.ProviderKey, _domain!.Id, "tlsrpt");
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build DotMarc.sln`
Expected: Build succeeds — this is the task that resolves the compile errors Tasks 1 and 3 left pending in this file.

- [ ] **Step 4: Manually verify**

There's no component test harness for Razor pages in this repo. If a reachable dev/demo environment is available, load a domain detail page whose DMARC status is Misconfigured and confirm: the confirm dialog now says "Push DMARC record" with the record name and delegation-or-diff content rendering correctly, Cancel doesn't push, Apply proceeds to the popup. If no environment is reachable, note that in your report as DONE_WITH_CONCERNS — final review will re-check the logic by reading the diff.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Components/Pages/DomainDetail.razor
git commit -m "Always live-check DMARC/TLSRPT before pushing, not just when cached status flags it"
```

---

### Task 5: DomainMtaStsPanel component

**Files:**
- Create: `src/DotMarc/Components/Shared/DomainMtaStsPanel.razor`

**Interfaces:**
- Consumes: `IMtaStsCnameLookup` (Task 2), `DnsRecordPushDecision.NeedsConfirmation` (Task 3), `ConfirmDnsRecordPushDialog` (Task 3), `DomainManagementService.SetMtaStsConfigAsync(DotMarcDbContext, int, bool, MtaStsMode, List<string>, int, CancellationToken)` (existing), `IMxHostsLookup.LookupAsync(string, CancellationToken)` (existing).
- Produces: a `[Parameter] public Domain Domain { get; set; }` and `[Parameter] public Func<string, int, string, Task> OpenDnsPushPopup { get; set; }` contract — Task 6 wires this into `DomainDetail.razor`, passing its own `_domain` and `OpenDnsPushPopupAsync` method group.

- [ ] **Step 1: Write the component**

Create `src/DotMarc/Components/Shared/DomainMtaStsPanel.razor`:

```razor
@using DotMarc.Data
@using DotMarc.DnsPush
@using DotMarc.MtaSts
@using DotMarc.Reporting
@using Microsoft.AspNetCore.Authorization
@using Microsoft.EntityFrameworkCore
@using Microsoft.Extensions.Options
@inject IDbContextFactory<DotMarcDbContext> DbFactory
@inject IOptions<MtaStsOptions> MtaStsOptions
@inject IMxHostsLookup MxHostsLookup
@inject IMtaStsCnameLookup MtaStsCnameLookup
@inject IDnsProviderDetector DnsProviderDetector
@inject IEnumerable<IDnsPushProvider> DnsPushProviders
@inject IDialogService DialogService
@inject ISnackbar Snackbar

<MudPaper Class="pa-4 my-2" Elevation="1">
    <MudText Typo="Typo.subtitle1" Class="mb-2">Policy hosting status</MudText>
    <MudChip T="string" Color="@MtaStsStatusPresentation.GetColor(Domain.MtaStsStatus)" Size="Size.Small">@MtaStsStatusPresentation.GetLabel(Domain.MtaStsStatus)</MudChip>
    @if (Domain.MtaStsCheckedUtc is { } checkedUtc)
    {
        <MudText Typo="Typo.caption" Class="mt-1">Last checked: @checkedUtc.ToString("yyyy-MM-dd HH:mm:ss")</MudText>
    }
    @if (!string.IsNullOrWhiteSpace(Domain.MtaStsCheckDetail))
    {
        <MudText Typo="Typo.body2" Class="mt-1">@Domain.MtaStsCheckDetail</MudText>
    }
    <MudLink Href="https://dotmarc.app/docs/mta-sts#status" Target="_blank" Class="mt-2 d-block">What do these statuses mean?</MudLink>

    <AuthorizeView Policy="MtaStsManage" Context="manageAuthState">
        <div class="mt-3">
            @if (!_enabled)
            {
                @if (_isEnabling)
                {
                    <MudProgressCircular Size="Size.Small" Indeterminate="true" />
                }
                else
                {
                    <MudButton Variant="Variant.Filled" Color="Color.Primary" OnClick="EnableAsync">Enable MTA-STS</MudButton>
                }
            }
            else
            {
                @if (Domain.MtaStsStatus == MtaStsStatus.PendingDns)
                {
                    @if (_isPushing)
                    {
                        <MudProgressCircular Size="Size.Small" Indeterminate="true" />
                    }
                    else
                    {
                        <MudButton Variant="Variant.Filled" Color="Color.Primary" StartIcon="@Icons.Material.Filled.CloudSync" OnClick="PushCnameAsync">Push CNAME via your DNS provider</MudButton>
                    }
                }
            }

            <MudExpansionPanels Class="mt-3">
                <MudExpansionPanel Text="Advanced">
                    <MudSelect T="MtaStsMode" @bind-Value="_mode" Label="Mode">
                        <MudSelectItem T="MtaStsMode" Value="MtaStsMode.Testing">Testing</MudSelectItem>
                        <MudSelectItem T="MtaStsMode" Value="MtaStsMode.Enforce">Enforce</MudSelectItem>
                        <MudSelectItem T="MtaStsMode" Value="MtaStsMode.None">None</MudSelectItem>
                    </MudSelect>
                    <MudNumericField T="int" @bind-Value="_maxAgeSeconds" Label="Max age (seconds)" Min="0" Class="mt-2" />
                    <MudTextField @bind-Value="_mxHostsText" Label="MX hosts" Lines="3" HelperText="One per line, e.g. mail.example.com" Class="mt-2" />
                    <MudButton Class="mt-2" OnClick="SaveAdvancedAsync">Save</MudButton>
                </MudExpansionPanel>
            </MudExpansionPanels>
        </div>
    </AuthorizeView>
</MudPaper>

@code {
    [Parameter, EditorRequired] public Domain Domain { get; set; } = default!;
    [Parameter, EditorRequired] public Func<string, int, string, Task> OpenDnsPushPopup { get; set; } = default!;

    private bool _enabled;
    private MtaStsMode _mode;
    private int _maxAgeSeconds;
    private string _mxHostsText = "";
    private bool _isEnabling;
    private bool _isPushing;

    protected override void OnParametersSet()
    {
        _enabled = Domain.MtaStsEnabled;
        _mode = Domain.MtaStsMode;
        _maxAgeSeconds = Domain.MtaStsMaxAgeSeconds;
        _mxHostsText = string.Join('\n', Domain.MtaStsMxHosts);
    }

    private async Task EnableAsync()
    {
        _isEnabling = true;
        try
        {
            var mxHosts = await MxHostsLookup.LookupAsync(Domain.Name, CancellationToken.None);
            if (mxHosts.Count == 0)
            {
                Snackbar.Add($"No MX records found for {Domain.Name} — add them manually in Advanced before enabling.", Severity.Warning);
                return;
            }

            await using var db = await DbFactory.CreateDbContextAsync();
            await DomainManagementService.SetMtaStsConfigAsync(db, Domain.Id, enabled: true, MtaStsMode.Testing, mxHosts, 604_800, CancellationToken.None);

            _enabled = true;
            _mode = MtaStsMode.Testing;
            _maxAgeSeconds = 604_800;
            _mxHostsText = string.Join('\n', mxHosts);
            Domain.MtaStsEnabled = true;
            Domain.MtaStsMode = MtaStsMode.Testing;
            Domain.MtaStsMaxAgeSeconds = 604_800;
            Domain.MtaStsMxHosts = mxHosts;
            Domain.MtaStsStatus = MtaStsStatus.PendingDns;

            Snackbar.Add($"MTA-STS enabled for {Domain.Name}.", Severity.Success);
            await PushCnameAsync();
        }
        catch (Exception)
        {
            Snackbar.Add($"Failed to enable MTA-STS for {Domain.Name}. Try again.", Severity.Error);
        }
        finally
        {
            _isEnabling = false;
        }
    }

    private async Task SaveAdvancedAsync()
    {
        var mxHosts = _mxHostsText
            .Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            await DomainManagementService.SetMtaStsConfigAsync(db, Domain.Id, _enabled, _mode, mxHosts, _maxAgeSeconds, CancellationToken.None);
            Domain.MtaStsMode = _mode;
            Domain.MtaStsMaxAgeSeconds = _maxAgeSeconds;
            Domain.MtaStsMxHosts = mxHosts;
            Snackbar.Add($"MTA-STS configuration saved for {Domain.Name}.", Severity.Success);
        }
        catch (Exception)
        {
            Snackbar.Add($"Failed to save {Domain.Name}'s MTA-STS configuration. Try again.", Severity.Error);
        }
    }

    private async Task PushCnameAsync()
    {
        _isPushing = true;
        try
        {
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
                Snackbar.Add($"Couldn't find a configured DNS push option for {Domain.Name} — add the CNAME manually.", Severity.Warning);
                return;
            }

            var hostingHostname = MtaStsOptions.Value.HostingHostname;
            if (string.IsNullOrEmpty(hostingHostname))
            {
                Snackbar.Add("MTA-STS hosting isn't configured on this deployment.", Severity.Warning);
                return;
            }

            string? existing;
            try
            {
                existing = await MtaStsCnameLookup.LookupAsync(Domain.Name, CancellationToken.None);
            }
            catch (Exception)
            {
                Snackbar.Add($"Failed to look up {Domain.Name}'s current MTA-STS CNAME. Try again.", Severity.Error);
                return;
            }

            if (DnsRecordPushDecision.NeedsConfirmation(existing, delegatedToCname: null, hostingHostname))
            {
                var parameters = new DialogParameters<ConfirmDnsRecordPushDialog>
                {
                    { x => x.RecordDescription, "MTA-STS CNAME" },
                    { x => x.RecordName, $"mta-sts.{Domain.Name}" },
                    { x => x.ExistingValue, existing ?? "" },
                    { x => x.ProposedValue, hostingHostname }
                };
                var dialogRef = await DialogService.ShowAsync<ConfirmDnsRecordPushDialog>("Push MTA-STS CNAME", parameters);
                var result = await dialogRef.Result;
                if (result is null || result.Canceled)
                {
                    return;
                }
            }

            await OpenDnsPushPopup(pushProvider.ProviderKey, Domain.Id, "mta-sts");
        }
        finally
        {
            _isPushing = false;
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/DotMarc/DotMarc.csproj`
Expected: This file is not yet referenced anywhere, so it compiles standalone once its dependencies (Tasks 2 and 3) are in place — expect success. `MudExpansionPanels`/`MudExpansionPanel` (with a `Text` header parameter) are confirmed present in this project's installed MudBlazor 9.8.0.

- [ ] **Step 3: Commit**

```bash
git add src/DotMarc/Components/Shared/DomainMtaStsPanel.razor
git commit -m "Add DomainMtaStsPanel: per-domain MTA-STS enable/configure/push UI"
```

---

### Task 6: Wire the panel into DomainDetail.razor, and give MTA-STS a real merge path server-side

**Files:**
- Modify: `src/DotMarc/Components/Pages/DomainDetail.razor`
- Modify: `src/DotMarc/Program.cs`

**Interfaces:**
- Consumes: `DomainMtaStsPanel` (Task 5), `IMtaStsCnameLookup` (Task 2).

- [ ] **Step 1: Host the panel in the MTA-STS tab**

Open `src/DotMarc/Components/Pages/DomainDetail.razor`. Find the MTA-STS tab panel:

```razor
        <MudTabPanel Text="MTA-STS">
            <AuthorizeView Policy="MtaStsView" Context="viewAuthState">
                <MudPaper Class="pa-4 my-2" Elevation="1">
                    <MudText Typo="Typo.subtitle1" Class="mb-2">Policy hosting status</MudText>
                    <MudChip T="string" Color="@MtaStsStatusPresentation.GetColor(_domain.MtaStsStatus)" Size="Size.Small">@MtaStsStatusPresentation.GetLabel(_domain.MtaStsStatus)</MudChip>
                    @if (_domain.MtaStsCheckedUtc is { } checkedUtc)
                    {
                        <MudText Typo="Typo.caption" Class="mt-1">Last checked: @checkedUtc.ToString("yyyy-MM-dd HH:mm:ss")</MudText>
                    }
                    @if (!string.IsNullOrWhiteSpace(_domain.MtaStsCheckDetail))
                    {
                        <MudText Typo="Typo.body2" Class="mt-1">@_domain.MtaStsCheckDetail</MudText>
                    }
                    <div class="mt-3">
                        <AuthorizeView Policy="MtaStsManage" Context="manageAuthState">
                            <MudButton Href="/mta-sts" Variant="Variant.Text" Color="Color.Primary">Manage MTA-STS settings</MudButton>
                        </AuthorizeView>
                        <MudLink Href="https://dotmarc.app/docs/mta-sts#status" Target="_blank" Class="ml-2">What do these statuses mean?</MudLink>
                    </div>
                </MudPaper>
            </AuthorizeView>
        </MudTabPanel>
```

Replace it with:

```razor
        <MudTabPanel Text="MTA-STS">
            <AuthorizeView Policy="MtaStsView" Context="viewAuthState">
                <DomainMtaStsPanel Domain="_domain" OpenDnsPushPopup="OpenDnsPushPopupAsync" />
            </AuthorizeView>
        </MudTabPanel>
```

Add `@using DotMarc.Components.Shared` to this file's existing `@using` block near the top if it isn't already present (check the existing `@using` lines first — this repo's other pages that reference `Components.Shared` types, like `UnsavedChangesGuard`, already carry this import; `ManageMtaSts.razor` has `@using DotMarc.Components.Shared` at its top as the precedent to match).

- [ ] **Step 2: Give MTA-STS a real merge path in Program.cs**

Open `src/DotMarc/Program.cs`. Find the `/dns-push/{provider}/callback` endpoint's parameter list and add the new lookup dependency:

```csharp
app.MapGet("/dns-push/{provider}/callback", async (
    string provider, string? code, string? state, string? error, HttpContext httpContext,
    IEnumerable<IDnsPushProvider> pushProviders, DnsPushStateProtector stateProtector,
    IDbContextFactory<DotMarcDbContext> dbContextFactory, IDmarcTxtLookup dmarcTxtLookup, ITlsrptTxtLookup tlsrptTxtLookup,
    IOptions<DotMarc.MtaSts.MtaStsOptions> mtaStsOptions, IOptions<GraphOptions> graphOptions,
    DotMarc.MtaSts.IMtaStsHostProvisioner mtaStsHostProvisioner,
    IAuthorizationService authorizationService) =>
```

Change it to:

```csharp
app.MapGet("/dns-push/{provider}/callback", async (
    string provider, string? code, string? state, string? error, HttpContext httpContext,
    IEnumerable<IDnsPushProvider> pushProviders, DnsPushStateProtector stateProtector,
    IDbContextFactory<DotMarcDbContext> dbContextFactory, IDmarcTxtLookup dmarcTxtLookup, ITlsrptTxtLookup tlsrptTxtLookup,
    IOptions<DotMarc.MtaSts.MtaStsOptions> mtaStsOptions, IOptions<GraphOptions> graphOptions,
    DotMarc.MtaSts.IMtaStsHostProvisioner mtaStsHostProvisioner, DotMarc.MtaSts.IMtaStsCnameLookup mtaStsCnameLookup,
    IAuthorizationService authorizationService) =>
```

Find the `mta-sts` branch:

```csharp
    List<DnsRecordChange> changes;
    if (decodedState.PushTarget == "mta-sts")
    {
        var hostingHostname = mtaStsOptions.Value.HostingHostname;
        if (string.IsNullOrEmpty(hostingHostname))
        {
            return DnsPushPopupResult.Close("error");
        }
        changes = [new DnsRecordChange(DnsRecordChangeKind.Create, "CNAME", $"mta-sts.{domain.Name}", hostingHostname, null, domain.Name)];

        // Azure Container Apps also needs a domain-ownership TXT record before it will bind the
        // custom domain — see AzureMtaStsHostProvisioner and the design spec's "Fetching the
        // verification ID" section. Caddy has no such requirement, and a null/empty ID (the ARM
        // call failed, or this deployment isn't actually Azure-provisioned) just means the push
        // proceeds with the CNAME alone rather than failing outright.
        if (string.Equals(mtaStsOptions.Value.Provisioner, "Azure", StringComparison.OrdinalIgnoreCase))
        {
            var verificationId = await mtaStsHostProvisioner.GetDomainVerificationIdAsync(CancellationToken.None);
            if (!string.IsNullOrEmpty(verificationId))
            {
                changes.Add(new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"asuid.mta-sts.{domain.Name}", verificationId, null, domain.Name));
            }
        }
    }
```

Replace it with:

```csharp
    List<DnsRecordChange> changes;
    if (decodedState.PushTarget == "mta-sts")
    {
        var hostingHostname = mtaStsOptions.Value.HostingHostname;
        if (string.IsNullOrEmpty(hostingHostname))
        {
            return DnsPushPopupResult.Close("error");
        }

        var existingCname = await mtaStsCnameLookup.LookupAsync(domain.Name, CancellationToken.None);
        var cnameChange = existingCname is null
            ? new DnsRecordChange(DnsRecordChangeKind.Create, "CNAME", $"mta-sts.{domain.Name}", hostingHostname, null, domain.Name)
            : new DnsRecordChange(DnsRecordChangeKind.Merge, "CNAME", $"mta-sts.{domain.Name}", hostingHostname, existingCname, domain.Name);
        changes = [cnameChange];

        // Azure Container Apps also needs a domain-ownership TXT record before it will bind the
        // custom domain — see AzureMtaStsHostProvisioner and the design spec's "Fetching the
        // verification ID" section. Caddy has no such requirement, and a null/empty ID (the ARM
        // call failed, or this deployment isn't actually Azure-provisioned) just means the push
        // proceeds with the CNAME alone rather than failing outright.
        if (string.Equals(mtaStsOptions.Value.Provisioner, "Azure", StringComparison.OrdinalIgnoreCase))
        {
            var verificationId = await mtaStsHostProvisioner.GetDomainVerificationIdAsync(CancellationToken.None);
            if (!string.IsNullOrEmpty(verificationId))
            {
                changes.Add(new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"asuid.mta-sts.{domain.Name}", verificationId, null, domain.Name));
            }
        }
    }
```

- [ ] **Step 3: Build and test**

Run: `dotnet build DotMarc.sln && dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj`
Expected: Build succeeds, all tests pass (412 existing + this plan's new tests, no regressions).

- [ ] **Step 4: Manually verify**

If a reachable environment exists, load a domain's MTA-STS tab: confirm the "Enable MTA-STS" button appears when disabled, enabling it auto-fetches MX hosts and (if a configured push provider is detected) offers the push, the Advanced expander shows/edits Mode/max-age/MX hosts, and Save there works independently of Enable. If unreachable, note DONE_WITH_CONCERNS.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Components/Pages/DomainDetail.razor src/DotMarc/Program.cs
git commit -m "Wire DomainMtaStsPanel into Domain Detail; give MTA-STS push a real merge path"
```

---

### Task 7: Remove the standalone Manage MTA-STS page

**Files:**
- Delete: `src/DotMarc/Components/Pages/ManageMtaSts.razor`
- Modify: `src/DotMarc/Components/Layout/MainLayout.razor`
- Modify: `website/docs/mta-sts.mdx`
- Modify: `website/docs/getting-started.mdx`

- [ ] **Step 1: Delete the page**

```bash
git rm src/DotMarc/Components/Pages/ManageMtaSts.razor
```

- [ ] **Step 2: Remove its nav link**

Open `src/DotMarc/Components/Layout/MainLayout.razor`. Find:

```razor
            <AuthorizeView Policy="MtaStsManage">
                <MudMenuItem Href="/mta-sts" Icon="@Icons.Material.Filled.Security">MTA-STS</MudMenuItem>
            </AuthorizeView>
```

Remove this block entirely (all 3 lines, including both `AuthorizeView` tags).

- [ ] **Step 3: Update mta-sts.mdx**

Open `website/docs/mta-sts.mdx`. Find the "Enabling a domain" section (reads roughly: "From **Manage MTA-STS**, toggle a domain on and add a CNAME for it..."). Read the file's current content around that section before editing — it was last touched earlier today (the asuid-record documentation work) and its exact current wording needs to be read fresh rather than assumed. Replace the reference to the standalone "Manage MTA-STS" page with a reference to the domain's own MTA-STS tab, e.g. "From a domain's **MTA-STS** tab, enable it and dotMARC will..." — keep the surrounding CNAME/asuid instructions intact, only the "where you go to do this" phrasing changes.

- [ ] **Step 4: Update getting-started.mdx**

Open `website/docs/getting-started.mdx`. Search for any reference to "Manage MTA-STS" (the page name) — the MTA-STS section there describes server-side hosting setup (Caddy, `MtaSts__HostingHostname`), not the per-domain enable UI, so it likely doesn't reference the page by name at all; if a reference is found, update it to point at the domain's MTA-STS tab the same way as Step 3. If no reference exists, no change is needed in this file — note that in your report rather than editing something that doesn't need it.

- [ ] **Step 5: Build and test**

Run: `dotnet build DotMarc.sln && dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj`
Expected: Build succeeds, all tests pass — confirms nothing else in the solution still references `ManageMtaSts` or routes to `/mta-sts`. Run `grep -rn "ManageMtaSts\|/mta-sts\b" src/DotMarc` first to double check no other file (e.g. a redirect, a breadcrumb) still references the removed page before considering this step done.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Remove the standalone Manage MTA-STS page now that domain pages handle it"
```
