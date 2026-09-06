# Domain Health Checklist Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split DMARC's collapsed own-record/authorization-record status into two independent checks, add new live SPF/MX/DKIM DNS checks, and replace the domain detail page's Overview tab with one consolidated health checklist covering all seven checks.

**Architecture:** Follow the exact pattern already established by `DmarcDnsChecker`/`TlsrptDnsChecker`/`PollingService`'s MTA-STS cycle: small, independent DNS-over-HTTPS checker classes returning a `(Status, Detail)` result record, each with its own status enum on `Domain`, its own `PollingService` cycle (advisory lock + staleness query + `internal static` single-domain method), and a `*StatusPresentation` static class mapping status to a MudBlazor color/label. The Overview tab UI is rebuilt around a new small reusable component (`DomainHealthCheckRow.razor`) invoked once per check, replacing the current two ad-hoc `MudPaper` panels.

**Tech Stack:** .NET 10, Blazor Server, EF Core + Npgsql, MudBlazor 9.8.0, xUnit + Testcontainers (Postgres).

**Spec:** `docs/superpowers/specs/2026-09-05-domain-health-checklist-design.md`

## Global Constraints

- Every new status enum's first member is its neutral/default value (`NotChecked` or, for DKIM, `NotConfigured`) — this is the enum's implicit default and therefore the new column's default for every existing row, requiring no data backfill. Follow `DmarcCheckStatus`'s existing doc-comment convention explaining why.
- Every new DNS-over-HTTPS checker is its own small, independent class querying `https://cloudflare-dns.com/` directly — do not introduce a shared base class or generic DNS-checking abstraction. This mirrors `DmarcTxtLookup`'s own doc comment: "small, independent DNS-over-HTTPS callers over a shared abstraction."
- No auto-push (no "Push via your DNS provider" button) for SPF or MX. Their correct values are domain-specific and dotMARC cannot compute them. Only DMARC record, DMARC authorization record, and TLSRPT keep push buttons.
- All new `Domain` status/detail/checked-timestamp fields follow the exact existing three-field-per-check shape: `{Check}Status`, `{Check}CheckedUtc` (`DateTimeOffset?`), `{Check}CheckDetail` (`string?`).
- Every new `internal static RunSingle*CheckAsync` method on `PollingService` must be directly callable from Blazor components in the same assembly (internal, not private) — this is what lets "recheck now" buttons reuse the exact scheduled-check logic, per the existing `RunSingleDmarcCheckAsync`/`RunSingleTlsrptCheckAsync`/`RunSingleMtaStsCheckAsync` precedent.
- Recheck buttons: `DomainsEdit` policy. Push buttons: `DomainsEdit` policy. DKIM's "Configure selectors" button: `DomainsEdit` policy. (All match existing gating already in `DomainDetail.razor`.)
- No changes to `Dashboard.razor` or its grouped DNS Status column — out of scope per the spec's Non-goals.

---

### Task 1: Data model — new enums, presentation helpers, Domain fields, migration

**Files:**
- Create: `src/DotMarc/Data/DmarcAuthorizationCheckStatus.cs`
- Create: `src/DotMarc/Data/SpfCheckStatus.cs`
- Create: `src/DotMarc/Data/MxCheckStatus.cs`
- Create: `src/DotMarc/Data/DkimCheckStatus.cs`
- Create: `src/DotMarc/Reporting/DmarcAuthorizationStatusPresentation.cs`
- Create: `src/DotMarc/Reporting/SpfStatusPresentation.cs`
- Create: `src/DotMarc/Reporting/MxStatusPresentation.cs`
- Create: `src/DotMarc/Reporting/DkimStatusPresentation.cs`
- Modify: `src/DotMarc/Data/Domain.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs`
- Create: EF Core migration (generated, not hand-written)

**Interfaces:**
- Produces: `DmarcAuthorizationCheckStatus { NotChecked, NotApplicable, Ok, Missing }`, `SpfCheckStatus { NotChecked, Ok, MissingRecord, MultipleRecords, Misconfigured }`, `MxCheckStatus { NotChecked, Ok, MissingRecord, UnresolvableTarget }`, `DkimCheckStatus { NotConfigured, Ok, Missing, Misconfigured }` — every later task's checker/UI code uses these exact enum names and member names.
- Produces: `Domain.DmarcAuthorizationCheckStatus`/`DmarcAuthorizationCheckedUtc`/`DmarcAuthorizationCheckDetail`, `Domain.SpfCheckStatus`/`SpfCheckedUtc`/`SpfCheckDetail`, `Domain.MxCheckStatus`/`MxCheckedUtc`/`MxCheckDetail`, `Domain.DkimSelectors` (`List<string>`, defaults to `[]`), `Domain.DkimCheckStatus`/`DkimCheckedUtc`/`DkimCheckDetail`.
- Produces: `DmarcAuthorizationStatusPresentation.GetColor(DmarcAuthorizationCheckStatus)`/`GetLabel(...)`, `SpfStatusPresentation.GetColor(SpfCheckStatus)`/`GetLabel(...)`, `MxStatusPresentation.GetColor(MxCheckStatus)`/`GetLabel(...)`, `DkimStatusPresentation.GetColor(DkimCheckStatus)`/`GetLabel(...)` — every one a `public static class` with exactly these two `public static` methods, matching `DmarcStatusPresentation`'s existing shape.

- [ ] **Step 1: Create the four new enum files**

`src/DotMarc/Data/DmarcAuthorizationCheckStatus.cs`:
```csharp
namespace DotMarc.Data;

/// <summary>The result of the most recent RFC 7489 §7.1 DMARC authorization-record check for a
/// Domain — see DotMarc.Dns.DmarcDnsChecker.CheckAuthorizationAsync. Split out from
/// DmarcCheckStatus so the own-record check and this one can be shown (and independently
/// corrected) regardless of the other's state; the old DmarcCheckStatus.MissingAuthorizationRecord
/// value stays defined for backward compatibility with existing rows but is never emitted again.
/// NotChecked is listed first so it is the enum's (and the database column's) default value.</summary>
public enum DmarcAuthorizationCheckStatus
{
    NotChecked,
    NotApplicable,
    Ok,
    Missing
}
```

`src/DotMarc/Data/SpfCheckStatus.cs`:
```csharp
namespace DotMarc.Data;

/// <summary>The result of the most recent SPF DNS check for a Domain — see
/// DotMarc.Dns.SpfDnsChecker. Only checks presence, record uniqueness, and the v=spf1 prefix; does
/// not validate the 10-DNS-lookup limit (RFC 7208) or the mechanism chain. NotChecked is listed
/// first so it is the enum's (and the database column's) default value.</summary>
public enum SpfCheckStatus
{
    NotChecked,
    Ok,
    MissingRecord,
    MultipleRecords,
    Misconfigured
}
```

`src/DotMarc/Data/MxCheckStatus.cs`:
```csharp
namespace DotMarc.Data;

/// <summary>The result of the most recent MX DNS check for a Domain — see
/// DotMarc.Dns.MxDnsChecker. An explicit RFC 7505 null MX ("0 .") counts as Ok — it's an
/// intentional "this domain sends but does not receive mail" policy, not a failure. NotChecked is
/// listed first so it is the enum's (and the database column's) default value.</summary>
public enum MxCheckStatus
{
    NotChecked,
    Ok,
    MissingRecord,
    UnresolvableTarget
}
```

`src/DotMarc/Data/DkimCheckStatus.cs`:
```csharp
namespace DotMarc.Data;

/// <summary>The result of the most recent DKIM DNS check for a Domain — see
/// DotMarc.Dns.DkimDnsChecker. Opt-in: dotMARC has no way to discover a domain's DKIM selector(s)
/// on its own, so this only ever runs once an admin configures at least one selector
/// (Domain.DkimSelectors). NotConfigured is listed first so it is the enum's (and the database
/// column's) default value — every existing domain starts here, which is a neutral state, not a
/// failure.</summary>
public enum DkimCheckStatus
{
    NotConfigured,
    Ok,
    Missing,
    Misconfigured
}
```

- [ ] **Step 2: Create the four new presentation helpers**

`src/DotMarc/Reporting/DmarcAuthorizationStatusPresentation.cs`:
```csharp
using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps DmarcAuthorizationCheckStatus to the MudBlazor color/label pair used on
/// DomainDetail.razor's Overview health checklist — same shared-presentation-logic precedent as
/// DmarcStatusPresentation.</summary>
public static class DmarcAuthorizationStatusPresentation
{
    public static Color GetColor(DmarcAuthorizationCheckStatus status) => status switch
    {
        DmarcAuthorizationCheckStatus.Ok or DmarcAuthorizationCheckStatus.NotApplicable => Color.Success,
        DmarcAuthorizationCheckStatus.Missing => Color.Warning,
        _ => Color.Default
    };

    public static string GetLabel(DmarcAuthorizationCheckStatus status) => status switch
    {
        DmarcAuthorizationCheckStatus.Ok => "OK",
        DmarcAuthorizationCheckStatus.NotApplicable => "Not applicable",
        DmarcAuthorizationCheckStatus.Missing => "Missing authorization",
        _ => "Not checked yet"
    };
}
```

`src/DotMarc/Reporting/SpfStatusPresentation.cs`:
```csharp
using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps SpfCheckStatus to the MudBlazor color/label pair used on DomainDetail.razor's
/// Overview health checklist — same shared-presentation-logic precedent as
/// DmarcStatusPresentation.</summary>
public static class SpfStatusPresentation
{
    public static Color GetColor(SpfCheckStatus status) => status switch
    {
        SpfCheckStatus.Ok => Color.Success,
        SpfCheckStatus.MultipleRecords => Color.Warning,
        SpfCheckStatus.MissingRecord or SpfCheckStatus.Misconfigured => Color.Error,
        _ => Color.Default
    };

    public static string GetLabel(SpfCheckStatus status) => status switch
    {
        SpfCheckStatus.Ok => "OK",
        SpfCheckStatus.MissingRecord => "No SPF record",
        SpfCheckStatus.MultipleRecords => "Multiple SPF records",
        SpfCheckStatus.Misconfigured => "Misconfigured",
        _ => "Not checked yet"
    };
}
```

`src/DotMarc/Reporting/MxStatusPresentation.cs`:
```csharp
using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps MxCheckStatus to the MudBlazor color/label pair used on DomainDetail.razor's
/// Overview health checklist — same shared-presentation-logic precedent as
/// DmarcStatusPresentation.</summary>
public static class MxStatusPresentation
{
    public static Color GetColor(MxCheckStatus status) => status switch
    {
        MxCheckStatus.Ok => Color.Success,
        MxCheckStatus.UnresolvableTarget => Color.Warning,
        MxCheckStatus.MissingRecord => Color.Error,
        _ => Color.Default
    };

    public static string GetLabel(MxCheckStatus status) => status switch
    {
        MxCheckStatus.Ok => "OK",
        MxCheckStatus.MissingRecord => "No MX record",
        MxCheckStatus.UnresolvableTarget => "Target does not resolve",
        _ => "Not checked yet"
    };
}
```

`src/DotMarc/Reporting/DkimStatusPresentation.cs`:
```csharp
using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps DkimCheckStatus to the MudBlazor color/label pair used on DomainDetail.razor's
/// Overview health checklist — same shared-presentation-logic precedent as
/// DmarcStatusPresentation.</summary>
public static class DkimStatusPresentation
{
    public static Color GetColor(DkimCheckStatus status) => status switch
    {
        DkimCheckStatus.Ok => Color.Success,
        DkimCheckStatus.Missing or DkimCheckStatus.Misconfigured => Color.Warning,
        _ => Color.Default
    };

    public static string GetLabel(DkimCheckStatus status) => status switch
    {
        DkimCheckStatus.Ok => "OK",
        DkimCheckStatus.Missing => "Selector record missing",
        DkimCheckStatus.Misconfigured => "Selector misconfigured",
        _ => "Not configured"
    };
}
```

- [ ] **Step 3: Add the new fields to Domain.cs**

In `src/DotMarc/Data/Domain.cs`, add after the existing `TlsrptCheckDetail` property (currently line 20) and before the `MtaStsEnabled` block:

```csharp
    public DmarcAuthorizationCheckStatus DmarcAuthorizationCheckStatus { get; set; }
    public DateTimeOffset? DmarcAuthorizationCheckedUtc { get; set; }
    public string? DmarcAuthorizationCheckDetail { get; set; }

    public SpfCheckStatus SpfCheckStatus { get; set; }
    public DateTimeOffset? SpfCheckedUtc { get; set; }
    public string? SpfCheckDetail { get; set; }

    public MxCheckStatus MxCheckStatus { get; set; }
    public DateTimeOffset? MxCheckedUtc { get; set; }
    public string? MxCheckDetail { get; set; }

    public List<string> DkimSelectors { get; set; } = [];
    public DkimCheckStatus DkimCheckStatus { get; set; }
    public DateTimeOffset? DkimCheckedUtc { get; set; }
    public string? DkimCheckDetail { get; set; }
```

- [ ] **Step 4: Configure the new fields in DotMarcDbContext**

In `src/DotMarc/Data/DotMarcDbContext.cs`, inside the existing `modelBuilder.Entity<Domain>(entity => { ... })` block, add after the existing `entity.Property(d => d.MtaStsMode).HasConversion<string>();` line:

```csharp
            entity.Property(d => d.DmarcAuthorizationCheckStatus).HasConversion<string>();
            entity.Property(d => d.SpfCheckStatus).HasConversion<string>();
            entity.Property(d => d.MxCheckStatus).HasConversion<string>();
            entity.Property(d => d.DkimCheckStatus).HasConversion<string>();
```

Then, immediately after the existing `entity.Property(d => d.MtaStsMxHosts).HasConversion(...).Metadata.SetValueComparer(...)` block (the one ending with the `MtaStsMxHosts` `ValueComparer`), add the same pattern for `DkimSelectors`:

```csharp
            entity.Property(d => d.DkimSelectors)
                .HasConversion(
                    selectors => selectors.ToArray(),
                    stored => stored.ToList())
                .Metadata.SetValueComparer(new ValueComparer<List<string>>(
                    (a, b) => (a ?? new()).SequenceEqual(b ?? new()),
                    c => c.Aggregate(0, (hash, h) => HashCode.Combine(hash, h)),
                    c => c.ToList()));
```

- [ ] **Step 5: Generate the EF Core migration**

Run from `src/DotMarc/`:
```bash
dotnet ef migrations add AddDomainHealthChecks
```

Open the generated migration file and confirm it adds exactly these columns to the `Domains` table, all nullable except the four status columns and `DkimSelectors` (which get their type's default — `NotChecked`/`NotConfigured` as a string, and `{}` as an empty array — via EF's normal non-nullable-column defaulting, matching how `MtaStsStatus`/`MtaStsMxHosts` were added in their own migration): `DmarcAuthorizationCheckStatus` (text), `DmarcAuthorizationCheckedUtc` (timestamptz, nullable), `DmarcAuthorizationCheckDetail` (text, nullable), `SpfCheckStatus` (text), `SpfCheckedUtc` (timestamptz, nullable), `SpfCheckDetail` (text, nullable), `MxCheckStatus` (text), `MxCheckedUtc` (timestamptz, nullable), `MxCheckDetail` (text, nullable), `DkimSelectors` (text array), `DkimCheckStatus` (text), `DkimCheckedUtc` (timestamptz, nullable), `DkimCheckDetail` (text, nullable).

- [ ] **Step 6: Build and run the full test suite**

```bash
dotnet build DotMarc.sln
dotnet test DotMarc.sln
```

Expected: clean build, all existing tests pass (the `[Collection("Postgres")]` tests apply every migration including this new one against a real Testcontainers Postgres instance, so a broken migration fails loudly here).

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Data/DmarcAuthorizationCheckStatus.cs src/DotMarc/Data/SpfCheckStatus.cs src/DotMarc/Data/MxCheckStatus.cs src/DotMarc/Data/DkimCheckStatus.cs src/DotMarc/Reporting/DmarcAuthorizationStatusPresentation.cs src/DotMarc/Reporting/SpfStatusPresentation.cs src/DotMarc/Reporting/MxStatusPresentation.cs src/DotMarc/Reporting/DkimStatusPresentation.cs src/DotMarc/Data/Domain.cs src/DotMarc/Data/DotMarcDbContext.cs src/DotMarc/Migrations/
git commit -m "Add data model for DMARC authorization, SPF, MX, and DKIM health checks"
```

---

### Task 2: DMARC authorization check split

**Files:**
- Create: `src/DotMarc/Dns/DmarcAuthorizationCheckResult.cs`
- Modify: `src/DotMarc/Dns/IDmarcDnsChecker.cs`
- Modify: `src/DotMarc/Dns/DmarcDnsChecker.cs`
- Modify: `test/DotMarc.Tests/Internal/FakeDmarcDnsChecker.cs`
- Test: `test/DotMarc.Tests/Dns/DmarcDnsCheckerTests.cs`

**Interfaces:**
- Consumes: `DmarcAuthorizationCheckStatus` (Task 1).
- Produces: `IDmarcDnsChecker.CheckAuthorizationAsync(string domainName, string mailboxAddress, CancellationToken) : Task<DmarcAuthorizationCheckResult>`, `DmarcAuthorizationCheckResult(DmarcAuthorizationCheckStatus Status, string? Detail)` — Task 6 (PollingService) calls this exact method.

- [ ] **Step 1: Create the result record**

`src/DotMarc/Dns/DmarcAuthorizationCheckResult.cs`:
```csharp
using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>The outcome of one DmarcDnsChecker.CheckAuthorizationAsync call. Detail is null exactly
/// when Status is Ok or NotApplicable — there's nothing to explain about a passing or
/// not-required check.</summary>
public sealed record DmarcAuthorizationCheckResult(DmarcAuthorizationCheckStatus Status, string? Detail);
```

- [ ] **Step 2: Update the two existing tests whose scenario moves to CheckAuthorizationAsync, and add the new tests**

`test/DotMarc.Tests/Dns/DmarcDnsCheckerTests.cs` currently has two tests that exercise `CheckAsync`'s OLD behavior of making a second request for the authorization record. Both need to change because `CheckAsync` no longer makes that second request at all — that check is now entirely `CheckAuthorizationAsync`'s job.

Replace this existing test (currently named `CheckAsync_ReturnsOk_WhenAuthorizationRecordIsPresent`, asserting `handler.Requests.Count == 2`):
```csharp
    [Fact]
    public async Task CheckAsync_ReturnsOk_WhenAuthorizationRecordIsPresent()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBodies.Enqueue("""
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk\""}]}
            """);
        handler.ResponseBodies.Enqueue("""
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1\""}]}
            """);

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Ok, result.Status);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("contoso.io._report._dmarc.mjco.uk", handler.Requests[1].RequestUri!.ToString());
    }
```
with:
```csharp
    [Fact]
    public async Task CheckAsync_ReturnsOk_AndMakesOnlyOneRequest_RegardlessOfMailboxDomain()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Ok, result.Status);
        Assert.Single(handler.Requests);
    }
```

Replace this existing test (currently named `CheckAsync_ReturnsMissingAuthorizationRecord_WhenAuthorizationRecordIsAbsent`, asserting the retired `DmarcCheckStatus.MissingAuthorizationRecord`):
```csharp
    [Fact]
    public async Task CheckAsync_ReturnsMissingAuthorizationRecord_WhenAuthorizationRecordIsAbsent()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBodies.Enqueue("""
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk\""}]}
            """);
        handler.ResponseBodies.Enqueue(NxDomainResponse);

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.MissingAuthorizationRecord, result.Status);
    }
```
with:
```csharp
    [Fact]
    public async Task CheckAsync_ReturnsOk_EvenWhenTheAuthorizationRecordWouldBeMissing()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; rua=mailto:rua.dmarc@mjco.uk\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Ok, result.Status);
        Assert.Single(handler.Requests);
    }
```

Then add these three new tests for `CheckAuthorizationAsync` (using the file's existing `CreateChecker()` helper, confirmed present at the top of the file):

```csharp
    [Fact]
    public async Task CheckAuthorizationAsync_ReturnsNotApplicable_WhenMailboxDomainMatchesMonitoredDomain()
    {
        var (checker, _) = CreateChecker();

        var result = await checker.CheckAuthorizationAsync("contoso.io", "dmarc@contoso.io", CancellationToken.None);

        Assert.Equal(DmarcAuthorizationCheckStatus.NotApplicable, result.Status);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task CheckAuthorizationAsync_ReturnsMissing_WhenNoAuthorizationRecordExists()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = NxDomainResponse;

        var result = await checker.CheckAuthorizationAsync("contoso.io", "dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcAuthorizationCheckStatus.Missing, result.Status);
        Assert.Contains("contoso.io._report._dmarc.mjco.uk", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task CheckAuthorizationAsync_ReturnsOk_WhenAuthorizationRecordExists()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1;\""}]}
            """;

        var result = await checker.CheckAuthorizationAsync("contoso.io", "dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcAuthorizationCheckStatus.Ok, result.Status);
        Assert.Null(result.Detail);
    }
```

(`NxDomainResponse` is the file's existing `private const string` at the top — reused, not redefined.)

- [ ] **Step 3: Run the tests to verify they fail to compile**

```bash
dotnet test DotMarc.sln --filter "FullyQualifiedName~DmarcDnsCheckerTests"
```
Expected: compile error, `CheckAuthorizationAsync` does not exist on `IDmarcDnsChecker`/`DmarcDnsChecker`.

- [ ] **Step 4: Update the interface**

In `src/DotMarc/Dns/IDmarcDnsChecker.cs`, add the new method:

```csharp
namespace DotMarc.Dns;

public interface IDmarcDnsChecker
{
    Task<DmarcCheckResult> CheckAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken);
    Task<DmarcAuthorizationCheckResult> CheckAuthorizationAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken);
}
```

- [ ] **Step 5: Update DmarcDnsChecker.cs**

In `src/DotMarc/Dns/DmarcDnsChecker.cs`, `CheckAsync`'s body currently ends with (lines 42-52):
```csharp
        var mailboxDomain = mailboxAddress[(mailboxAddress.IndexOf('@') + 1)..];
        if (string.Equals(mailboxDomain, domainName, StringComparison.OrdinalIgnoreCase))
        {
            return new DmarcCheckResult(DmarcCheckStatus.Ok, null);
        }

        var authorizationName = $"{domainName}._report._dmarc.{mailboxDomain}";
        var authorizationRecord = await QueryTxtAsync(authorizationName, cancellationToken).ConfigureAwait(false);
        return authorizationRecord is null
            ? new DmarcCheckResult(DmarcCheckStatus.MissingAuthorizationRecord, $"No TXT record found at {authorizationName}")
            : new DmarcCheckResult(DmarcCheckStatus.Ok, null);
    }
```

Replace that entire block with:
```csharp
        return new DmarcCheckResult(DmarcCheckStatus.Ok, null);
    }

    /// <summary>RFC 7489 §7.1: when the rua= mailbox's domain differs from the domain being
    /// monitored (the normal MSP shape), that mailbox's domain must publish this record proving it
    /// accepts reports for the monitored domain. Independent of CheckAsync above — this always
    /// runs and always returns its own result, regardless of whether CheckAsync's own-record check
    /// passed or failed, so both can be shown (and separately corrected) at once.</summary>
    public async Task<DmarcAuthorizationCheckResult> CheckAuthorizationAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken)
    {
        var mailboxDomain = mailboxAddress[(mailboxAddress.IndexOf('@') + 1)..];
        if (string.Equals(mailboxDomain, domainName, StringComparison.OrdinalIgnoreCase))
        {
            return new DmarcAuthorizationCheckResult(DmarcAuthorizationCheckStatus.NotApplicable, null);
        }

        var authorizationName = $"{domainName}._report._dmarc.{mailboxDomain}";
        var authorizationRecord = await QueryTxtAsync(authorizationName, cancellationToken).ConfigureAwait(false);
        return authorizationRecord is null
            ? new DmarcAuthorizationCheckResult(DmarcAuthorizationCheckStatus.Missing, $"No TXT record found at {authorizationName}")
            : new DmarcAuthorizationCheckResult(DmarcAuthorizationCheckStatus.Ok, null);
    }
```

(`QueryTxtAsync` is the class's existing private helper, reused unchanged.)

- [ ] **Step 6: Update FakeDmarcDnsChecker so the build doesn't break**

`test/DotMarc.Tests/Internal/FakeDmarcDnsChecker.cs` implements `IDmarcDnsChecker` and is used by `test/DotMarc.Tests/Ingestion/DmarcCheckCycleTests.cs` (a PollingService-level test file, unrelated to this task otherwise). The moment Step 4 adds `CheckAuthorizationAsync` to the interface, this fake stops compiling — fix it now, in this same task, before running anything, or the whole test suite fails to build:

```csharp
using DotMarc.Data;
using DotMarc.Dns;

namespace DotMarc.Tests.Internal;

internal sealed class FakeDmarcDnsChecker : IDmarcDnsChecker
{
    public DmarcCheckResult Result { get; set; } = new(DmarcCheckStatus.Ok, null);
    public bool ShouldThrow { get; set; }
    public List<string> CheckedDomains { get; } = [];

    public DmarcAuthorizationCheckResult AuthorizationResult { get; set; } = new(DmarcAuthorizationCheckStatus.NotApplicable, null);
    public bool AuthorizationShouldThrow { get; set; }
    public List<string> AuthorizationCheckedDomains { get; } = [];

    public Task<DmarcCheckResult> CheckAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken)
    {
        CheckedDomains.Add(domainName);
        if (ShouldThrow)
        {
            throw new HttpRequestException("Simulated Cloudflare failure.");
        }
        return Task.FromResult(Result);
    }

    public Task<DmarcAuthorizationCheckResult> CheckAuthorizationAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken)
    {
        AuthorizationCheckedDomains.Add(domainName);
        if (AuthorizationShouldThrow)
        {
            throw new HttpRequestException("Simulated Cloudflare failure.");
        }
        return Task.FromResult(AuthorizationResult);
    }
}
```

(The new `Authorization*` members are additive and independent of the existing `Result`/`ShouldThrow`/`CheckedDomains` — every existing test that only ever calls `CheckAsync` keeps working unchanged. Task 6 configures and asserts on the new members.)

- [ ] **Step 7: Run the tests to verify they pass**

```bash
dotnet test DotMarc.sln --filter "FullyQualifiedName~DmarcDnsCheckerTests"
```
Expected: all pass — the three new `CheckAuthorizationAsync` tests, the two rewritten `CheckAsync` tests from Step 2, and every other pre-existing `CheckAsync` test unchanged.

- [ ] **Step 8: Commit**

```bash
git add src/DotMarc/Dns/DmarcAuthorizationCheckResult.cs src/DotMarc/Dns/IDmarcDnsChecker.cs src/DotMarc/Dns/DmarcDnsChecker.cs test/DotMarc.Tests/Internal/FakeDmarcDnsChecker.cs test/DotMarc.Tests/Dns/DmarcDnsCheckerTests.cs
git commit -m "Split DMARC authorization record check into its own independent method"
```

---

### Task 3: SPF checker

**Files:**
- Create: `src/DotMarc/Dns/ISpfDnsChecker.cs`
- Create: `src/DotMarc/Dns/SpfDnsChecker.cs`
- Create: `src/DotMarc/Dns/SpfCheckResult.cs`
- Test: `test/DotMarc.Tests/Dns/SpfDnsCheckerTests.cs`

**Interfaces:**
- Consumes: `SpfCheckStatus` (Task 1).
- Produces: `ISpfDnsChecker.CheckAsync(string domainName, CancellationToken) : Task<SpfCheckResult>`, `SpfCheckResult(SpfCheckStatus Status, string? Detail)` — Task 6 (PollingService) and Task 8 (DI registration) consume these exact names.

- [ ] **Step 1: Create the result record**

`src/DotMarc/Dns/SpfCheckResult.cs`:
```csharp
using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>The outcome of one SpfDnsChecker.CheckAsync call. Detail is null exactly when Status is
/// Ok — there's nothing to explain about a passing check.</summary>
public sealed record SpfCheckResult(SpfCheckStatus Status, string? Detail);
```

- [ ] **Step 2: Create the interface**

`src/DotMarc/Dns/ISpfDnsChecker.cs`:
```csharp
namespace DotMarc.Dns;

public interface ISpfDnsChecker
{
    Task<SpfCheckResult> CheckAsync(string domainName, CancellationToken cancellationToken);
}
```

- [ ] **Step 3: Write the failing tests**

`test/DotMarc.Tests/Dns/SpfDnsCheckerTests.cs`:
```csharp
using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class SpfDnsCheckerTests
{
    private static (SpfDnsChecker checker, FakeHttpMessageHandler handler) CreateChecker()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new SpfDnsChecker(http), handler);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMissingRecord_WhenNoTxtRecordsExist()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """{"Status":3}""";

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(SpfCheckStatus.MissingRecord, result.Status);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMissingRecord_WhenTxtRecordsExistButNoneStartWithVEqualsSpf1()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"some-other-txt-record\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(SpfCheckStatus.MissingRecord, result.Status);
    }

    [Fact]
    public async Task CheckAsync_ReturnsOk_WhenExactlyOneSpfRecordExists()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=spf1 include:_spf.google.com ~all\""},{"type":16,"data":"\"some-other-txt-record\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(SpfCheckStatus.Ok, result.Status);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMultipleRecords_WhenTwoSpfRecordsExist()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=spf1 include:_spf.google.com ~all\""},{"type":16,"data":"\"v=spf1 include:spf.protection.outlook.com -all\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(SpfCheckStatus.MultipleRecords, result.Status);
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

```bash
dotnet test DotMarc.sln --filter "FullyQualifiedName~SpfDnsCheckerTests"
```
Expected: compile error, `SpfDnsChecker` does not exist.

- [ ] **Step 5: Implement SpfDnsChecker**

`src/DotMarc/Dns/SpfDnsChecker.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>Checks SPF (RFC 7208) record health at the monitored domain's apex: presence, that
/// exactly one v=spf1 TXT record exists (RFC 7208 requires exactly one — multiple is a common,
/// real misconfiguration), and the v=spf1 prefix itself. Does not follow include:/redirect= chains
/// or validate the 10-DNS-lookup limit — out of scope, see the design spec's Non-goals.</summary>
public sealed class SpfDnsChecker : ISpfDnsChecker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public SpfDnsChecker(HttpClient http) => _http = http;

    public async Task<SpfCheckResult> CheckAsync(string domainName, CancellationToken cancellationToken)
    {
        var allTxtRecords = await QueryAllTxtAsync(domainName, cancellationToken).ConfigureAwait(false);
        var spfRecords = allTxtRecords.Where(r => r.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)).ToList();

        if (spfRecords.Count == 0)
        {
            return new SpfCheckResult(SpfCheckStatus.MissingRecord, $"No SPF (v=spf1) TXT record found at {domainName}");
        }
        if (spfRecords.Count > 1)
        {
            return new SpfCheckResult(SpfCheckStatus.MultipleRecords, $"{domainName} has {spfRecords.Count} SPF records — RFC 7208 requires exactly one");
        }
        return new SpfCheckResult(SpfCheckStatus.Ok, null);
    }

    /// <summary>Unlike DmarcDnsChecker/TlsrptDnsChecker's QueryTxtAsync (which only returns the
    /// first TXT answer), this returns every TXT record at the name — detecting "multiple SPF
    /// records" requires seeing all of them, not just the first.</summary>
    private async Task<List<string>> QueryAllTxtAsync(string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type=TXT");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;
        return (parsed.Answer ?? [])
            .Where(a => a.Type == 16)
            .Select(a => string.Join("", a.Data.Split("\" \"")).Trim('"'))
            .ToList();
    }

    private sealed record DnsOverHttpsResponse([property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet test DotMarc.sln --filter "FullyQualifiedName~SpfDnsCheckerTests"
```
Expected: all 4 pass.

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Dns/ISpfDnsChecker.cs src/DotMarc/Dns/SpfDnsChecker.cs src/DotMarc/Dns/SpfCheckResult.cs test/DotMarc.Tests/Dns/SpfDnsCheckerTests.cs
git commit -m "Add SPF DNS health checker"
```

---

### Task 4: MX checker

**Files:**
- Create: `src/DotMarc/Dns/IMxDnsChecker.cs`
- Create: `src/DotMarc/Dns/MxDnsChecker.cs`
- Create: `src/DotMarc/Dns/MxCheckResult.cs`
- Test: `test/DotMarc.Tests/Dns/MxDnsCheckerTests.cs`

**Interfaces:**
- Consumes: `MxCheckStatus` (Task 1).
- Produces: `IMxDnsChecker.CheckAsync(string domainName, CancellationToken) : Task<MxCheckResult>`, `MxCheckResult(MxCheckStatus Status, string? Detail)` — Task 6 and Task 8 consume these exact names.

- [ ] **Step 1: Create the result record**

`src/DotMarc/Dns/MxCheckResult.cs`:
```csharp
using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>The outcome of one MxDnsChecker.CheckAsync call. Detail is null exactly when Status is
/// Ok and it's a normal (non-null-MX) result — there's nothing to explain about a passing check.
/// An explicit null MX is Ok but still carries an explanatory Detail (see MxDnsChecker), since
/// "no mail servers" reads as suspicious without the RFC 7505 context.</summary>
public sealed record MxCheckResult(MxCheckStatus Status, string? Detail);
```

- [ ] **Step 2: Create the interface**

`src/DotMarc/Dns/IMxDnsChecker.cs`:
```csharp
namespace DotMarc.Dns;

public interface IMxDnsChecker
{
    Task<MxCheckResult> CheckAsync(string domainName, CancellationToken cancellationToken);
}
```

- [ ] **Step 3: Write the failing tests**

`test/DotMarc.Tests/Dns/MxDnsCheckerTests.cs`:
```csharp
using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class MxDnsCheckerTests
{
    private static (MxDnsChecker checker, FakeHttpMessageHandler handler) CreateChecker()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new MxDnsChecker(http), handler);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMissingRecord_WhenNoMxRecordsExist()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """{"Status":3}""";

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(MxCheckStatus.MissingRecord, result.Status);
    }

    [Fact]
    public async Task CheckAsync_ReturnsOk_WhenNullMxIsPublished()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":15,"data":"0 ."}]}
            """;

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(MxCheckStatus.Ok, result.Status);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public async Task CheckAsync_ReturnsOk_WhenTheMxTargetResolves()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBodies.Enqueue("""{"Status":0,"Answer":[{"type":15,"data":"10 mail.contoso.io."}]}""");
        handler.ResponseBodies.Enqueue("""{"Status":0,"Answer":[{"type":1,"data":"192.0.2.10"}]}""");

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(MxCheckStatus.Ok, result.Status);
        Assert.Null(result.Detail);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("type=MX", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("mail.contoso.io", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task CheckAsync_ReturnsUnresolvableTarget_WhenTheMxTargetHasNoARecord()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBodies.Enqueue("""{"Status":0,"Answer":[{"type":15,"data":"10 mail.contoso.io."}]}""");
        handler.ResponseBodies.Enqueue("""{"Status":3}""");

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(MxCheckStatus.UnresolvableTarget, result.Status);
        Assert.Contains("mail.contoso.io", result.Detail);
    }
}
```

`FakeHttpMessageHandler` (`test/DotMarc.Tests/Internal/FakeHttpMessageHandler.cs`) already supports this: its `ResponseBodies` is a `Queue<string>` — each request dequeues the next body in order, falling back to the fixed `ResponseBody` once drained. `MxDnsChecker.CheckAsync` makes the MX lookup first, then one A-record lookup per MX target in the order `QueryMxAsync` returns them, so enqueuing MX-response-then-A-response matches that exact call order. Do not invent a new mechanism on `FakeHttpMessageHandler` — this one already covers it.

- [ ] **Step 4: Run the tests to verify they fail**

```bash
dotnet test DotMarc.sln --filter "FullyQualifiedName~MxDnsCheckerTests"
```
Expected: compile error, `MxDnsChecker` does not exist.

- [ ] **Step 5: Implement MxDnsChecker**

`src/DotMarc/Dns/MxDnsChecker.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>Checks MX record health at the monitored domain's apex: presence, an explicit RFC 7505
/// null MX ("0 .") counted as an intentional "this domain does not receive mail" policy rather
/// than a failure, and that every real MX target actually resolves. Does its own raw MX query
/// rather than reusing DotMarc.MtaSts.IMxHostsLookup, which trims/dedupes for a different purpose
/// (pre-filling MTA-STS policy tags) and would collapse a null MX's "." exchange to an empty
/// string, losing the distinction this checker needs to make.</summary>
public sealed class MxDnsChecker : IMxDnsChecker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public MxDnsChecker(HttpClient http) => _http = http;

    public async Task<MxCheckResult> CheckAsync(string domainName, CancellationToken cancellationToken)
    {
        var mxAnswers = await QueryMxAsync(domainName, cancellationToken).ConfigureAwait(false);

        if (mxAnswers.Count == 0)
        {
            return new MxCheckResult(MxCheckStatus.MissingRecord, $"No MX record found at {domainName}");
        }
        if (mxAnswers.Count == 1 && mxAnswers[0].Exchange == ".")
        {
            return new MxCheckResult(MxCheckStatus.Ok, "Explicit null MX (RFC 7505) — this domain intentionally does not accept mail.");
        }

        var unresolvable = new List<string>();
        foreach (var (_, exchange) in mxAnswers)
        {
            var host = exchange.TrimEnd('.');
            if (!await ResolvesAsync(host, cancellationToken).ConfigureAwait(false))
            {
                unresolvable.Add(host);
            }
        }

        return unresolvable.Count > 0
            ? new MxCheckResult(MxCheckStatus.UnresolvableTarget, $"MX target(s) do not resolve: {string.Join(", ", unresolvable)}")
            : new MxCheckResult(MxCheckStatus.Ok, null);
    }

    private async Task<List<(int Preference, string Exchange)>> QueryMxAsync(string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type=MX");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;
        return (parsed.Answer ?? [])
            .Where(a => a.Type == 15)
            .Select(a =>
            {
                var parts = a.Data.Split(' ', 2);
                return (Preference: int.Parse(parts[0]), Exchange: parts[1]);
            })
            .ToList();
    }

    private async Task<bool> ResolvesAsync(string host, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(host)}&type=A");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;
        return (parsed.Answer ?? []).Any(a => a.Type == 1);
    }

    private sealed record DnsOverHttpsResponse([property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet test DotMarc.sln --filter "FullyQualifiedName~MxDnsCheckerTests"
```
Expected: all 4 pass.

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Dns/IMxDnsChecker.cs src/DotMarc/Dns/MxDnsChecker.cs src/DotMarc/Dns/MxCheckResult.cs test/DotMarc.Tests/Dns/MxDnsCheckerTests.cs
git commit -m "Add MX DNS health checker"
```

---

### Task 5: DKIM checker

**Files:**
- Create: `src/DotMarc/Dns/IDkimDnsChecker.cs`
- Create: `src/DotMarc/Dns/DkimDnsChecker.cs`
- Create: `src/DotMarc/Dns/DkimCheckResult.cs`
- Test: `test/DotMarc.Tests/Dns/DkimDnsCheckerTests.cs`

**Interfaces:**
- Consumes: `DkimCheckStatus` (Task 1).
- Produces: `IDkimDnsChecker.CheckAsync(string domainName, IReadOnlyList<string> selectors, CancellationToken) : Task<DkimCheckResult>`, `DkimCheckResult(DkimCheckStatus Status, string? Detail)` — Task 6 and Task 8 consume these exact names. Note the extra `selectors` parameter, unlike every other checker in this plan.

- [ ] **Step 1: Create the result record**

`src/DotMarc/Dns/DkimCheckResult.cs`:
```csharp
using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>The outcome of one DkimDnsChecker.CheckAsync call. Detail is null exactly when Status
/// is Ok — there's nothing to explain about a passing check.</summary>
public sealed record DkimCheckResult(DkimCheckStatus Status, string? Detail);
```

- [ ] **Step 2: Create the interface**

`src/DotMarc/Dns/IDkimDnsChecker.cs`:
```csharp
namespace DotMarc.Dns;

public interface IDkimDnsChecker
{
    Task<DkimCheckResult> CheckAsync(string domainName, IReadOnlyList<string> selectors, CancellationToken cancellationToken);
}
```

- [ ] **Step 3: Write the failing tests**

`test/DotMarc.Tests/Dns/DkimDnsCheckerTests.cs`:
```csharp
using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class DkimDnsCheckerTests
{
    private static (DkimDnsChecker checker, FakeHttpMessageHandler handler) CreateChecker()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new DkimDnsChecker(http), handler);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMissing_WhenTheSelectorHasNoTxtRecord()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """{"Status":3}""";

        var result = await checker.CheckAsync("contoso.io", ["selector1"], CancellationToken.None);

        Assert.Equal(DkimCheckStatus.Missing, result.Status);
        Assert.Contains("selector1", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMisconfigured_WhenTheRecordHasNoPTag()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DKIM1; k=rsa\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", ["selector1"], CancellationToken.None);

        Assert.Equal(DkimCheckStatus.Misconfigured, result.Status);
        Assert.Contains("selector1", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_ReturnsOk_WhenTheRecordHasAPTag()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DKIM1; k=rsa; p=MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQC7\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", ["selector1"], CancellationToken.None);

        Assert.Equal(DkimCheckStatus.Ok, result.Status);
        Assert.Null(result.Detail);
        Assert.Contains("selector1._domainkey.contoso.io", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task CheckAsync_ReturnsMissing_WhenOneOfTwoSelectorsHasNoRecord()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBodies.Enqueue("""
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DKIM1; k=rsa; p=MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQC7\""}]}
            """);
        handler.ResponseBodies.Enqueue("""{"Status":3}""");

        var result = await checker.CheckAsync("contoso.io", ["selector1", "selector2"], CancellationToken.None);

        Assert.Equal(DkimCheckStatus.Missing, result.Status);
        Assert.Contains("selector2", result.Detail);
        Assert.DoesNotContain("selector1", result.Detail);
        Assert.Equal(2, handler.Requests.Count);
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

```bash
dotnet test DotMarc.sln --filter "FullyQualifiedName~DkimDnsCheckerTests"
```
Expected: compile error, `DkimDnsChecker` does not exist.

- [ ] **Step 5: Implement DkimDnsChecker**

`src/DotMarc/Dns/DkimDnsChecker.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>Checks DKIM selector record(s) at &lt;selector&gt;._domainkey.&lt;domain&gt;. Unlike
/// every other checker in this feature, this one is opt-in and takes the caller-supplied selector
/// list directly — dotMARC has no way to discover a domain's DKIM selector(s) on its own (they are
/// provider-specific strings with no DNS-discoverable convention), so this never guesses.</summary>
public sealed class DkimDnsChecker : IDkimDnsChecker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public DkimDnsChecker(HttpClient http) => _http = http;

    public async Task<DkimCheckResult> CheckAsync(string domainName, IReadOnlyList<string> selectors, CancellationToken cancellationToken)
    {
        var missing = new List<string>();
        var misconfigured = new List<string>();

        foreach (var selector in selectors)
        {
            var recordName = $"{selector}._domainkey.{domainName}";
            var record = await QueryTxtAsync(recordName, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                missing.Add(selector);
            }
            else if (!record.Contains("p=", StringComparison.OrdinalIgnoreCase))
            {
                misconfigured.Add(selector);
            }
        }

        if (missing.Count > 0)
        {
            return new DkimCheckResult(DkimCheckStatus.Missing, $"No DKIM record found for selector(s): {string.Join(", ", missing)}");
        }
        if (misconfigured.Count > 0)
        {
            return new DkimCheckResult(DkimCheckStatus.Misconfigured, $"Selector(s) missing a p= public-key tag: {string.Join(", ", misconfigured)}");
        }
        return new DkimCheckResult(DkimCheckStatus.Ok, null);
    }

    private async Task<string?> QueryTxtAsync(string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type=TXT");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;
        var answer = parsed.Answer?.FirstOrDefault(a => a.Type == 16);
        return answer is null ? null : string.Join("", answer.Data.Split("\" \"")).Trim('"');
    }

    private sealed record DnsOverHttpsResponse([property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet test DotMarc.sln --filter "FullyQualifiedName~DkimDnsCheckerTests"
```
Expected: all 4 pass.

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Dns/IDkimDnsChecker.cs src/DotMarc/Dns/DkimDnsChecker.cs src/DotMarc/Dns/DkimCheckResult.cs test/DotMarc.Tests/Dns/DkimDnsCheckerTests.cs
git commit -m "Add DKIM DNS health checker"
```

---

### Task 6: PollingService wiring and DI registration

**Files:**
- Modify: `src/DotMarc/Ingestion/PollingService.cs`
- Modify: `src/DotMarc/Program.cs`
- Test: `test/DotMarc.Tests/Ingestion/PollingServiceTests.cs`

**Interfaces:**
- Consumes: `IDmarcDnsChecker.CheckAuthorizationAsync` (Task 2), `ISpfDnsChecker.CheckAsync` (Task 3), `IMxDnsChecker.CheckAsync` (Task 4), `IDkimDnsChecker.CheckAsync` (Task 5), `Domain.DmarcAuthorizationCheckStatus`/`SpfCheckStatus`/`MxCheckStatus`/`DkimSelectors`/`DkimCheckStatus` and their paired `CheckedUtc`/`CheckDetail` fields (Task 1).
- Produces: `PollingService.RunDmarcAuthorizationCheckCycleAsync`, `RunSpfCheckCycleAsync`, `RunMxCheckCycleAsync`, `RunDkimCheckCycleAsync` (all `internal async Task`, same signature shape as `RunDmarcCheckCycleAsync`/`RunTlsrptCheckCycleAsync`), and `RunSingleDmarcAuthorizationCheckAsync`, `RunSingleSpfCheckAsync`, `RunSingleMxCheckAsync`, `RunSingleDkimCheckAsync` (all `internal static async Task`) — Task 9 (UI) calls these four `RunSingle*` methods directly by these exact names.

- [ ] **Step 1: Add the four new lock key constants**

In `src/DotMarc/Ingestion/PollingService.cs`, after the existing `internal const long TlsrptPollingLeaderLockKey = 84_200_009;` line, add:

```csharp
    /// <summary>Arbitrary fixed key for this service's DMARC-authorization-check advisory lock —
    /// independent of DmarcCheckLeaderLockKey since the own-record check and the authorization
    /// check are now two fully independent checks with their own staleness tracking.</summary>
    internal const long DmarcAuthorizationCheckLeaderLockKey = 84_200_011;

    internal const long SpfCheckLeaderLockKey = 84_200_013;

    internal const long MxCheckLeaderLockKey = 84_200_015;

    /// <summary>Arbitrary fixed key for this service's DKIM-check advisory lock. Runs on the same
    /// schedule as every other check even though most domains will have no selectors configured
    /// yet (RunSingleDkimCheckAsync short-circuits to NotConfigured in that case) — simpler than
    /// trying to filter the staleness query by DkimSelectors.Count, which doesn't translate cleanly
    /// through the List&lt;string&gt; value converter.</summary>
    internal const long DkimCheckLeaderLockKey = 84_200_017;
```

- [ ] **Step 2: Add the four new single-domain check methods**

Immediately after the existing `internal static async Task RunSingleTlsrptCheckAsync(...)` method (added in an earlier feature — it sits right before `internal async Task RunTlsrptPollCycleAsync(...)`), add:

```csharp
    /// <summary>DMARC-authorization counterpart to RunSingleDmarcCheckAsync — see its remarks.
    /// Always calls CheckAuthorizationAsync regardless of the (separate) own-record DMARC status,
    /// so the two are independently accurate.</summary>
    internal static async Task RunSingleDmarcAuthorizationCheckAsync(Domain domain, IDmarcDnsChecker dmarcChecker, string mailboxAddress, CancellationToken cancellationToken)
    {
        var result = await dmarcChecker.CheckAuthorizationAsync(domain.Name, mailboxAddress, cancellationToken).ConfigureAwait(false);
        domain.DmarcAuthorizationCheckStatus = result.Status;
        domain.DmarcAuthorizationCheckedUtc = DateTimeOffset.UtcNow;
        domain.DmarcAuthorizationCheckDetail = result.Detail;
    }

    /// <summary>SPF counterpart to RunSingleDmarcCheckAsync — see its remarks.</summary>
    internal static async Task RunSingleSpfCheckAsync(Domain domain, ISpfDnsChecker spfChecker, CancellationToken cancellationToken)
    {
        var result = await spfChecker.CheckAsync(domain.Name, cancellationToken).ConfigureAwait(false);
        domain.SpfCheckStatus = result.Status;
        domain.SpfCheckedUtc = DateTimeOffset.UtcNow;
        domain.SpfCheckDetail = result.Detail;
    }

    /// <summary>MX counterpart to RunSingleDmarcCheckAsync — see its remarks.</summary>
    internal static async Task RunSingleMxCheckAsync(Domain domain, IMxDnsChecker mxChecker, CancellationToken cancellationToken)
    {
        var result = await mxChecker.CheckAsync(domain.Name, cancellationToken).ConfigureAwait(false);
        domain.MxCheckStatus = result.Status;
        domain.MxCheckedUtc = DateTimeOffset.UtcNow;
        domain.MxCheckDetail = result.Detail;
    }

    /// <summary>DKIM is opt-in: with no selectors configured, this short-circuits to NotConfigured
    /// (a neutral default, not a failure) without calling the checker at all.</summary>
    internal static async Task RunSingleDkimCheckAsync(Domain domain, IDkimDnsChecker dkimChecker, CancellationToken cancellationToken)
    {
        if (domain.DkimSelectors.Count == 0)
        {
            domain.DkimCheckStatus = DkimCheckStatus.NotConfigured;
            domain.DkimCheckedUtc = DateTimeOffset.UtcNow;
            domain.DkimCheckDetail = null;
            return;
        }

        var result = await dkimChecker.CheckAsync(domain.Name, domain.DkimSelectors, cancellationToken).ConfigureAwait(false);
        domain.DkimCheckStatus = result.Status;
        domain.DkimCheckedUtc = DateTimeOffset.UtcNow;
        domain.DkimCheckDetail = result.Detail;
    }
```

- [ ] **Step 3: Add the four new cycle methods**

Immediately after the four methods added in Step 2, add (each mirrors `RunDmarcCheckCycleAsync`'s exact shape — its own advisory lock, a 24h staleness cutoff, a try/catch-continue loop, one save):

```csharp
    /// <summary>Runs a DMARC authorization-record check for every domain whose last check
    /// (DmarcAuthorizationCheckedUtc) is null or more than 24 hours old.</summary>
    internal async Task RunDmarcAuthorizationCheckCycleAsync(DotMarcDbContext context, IDmarcDnsChecker dmarcChecker, string mailboxAddress, CancellationToken cancellationToken)
    {
        var connectionString = context.Database.GetConnectionString()
            ?? throw new InvalidOperationException("DotMarcDbContext has no connection string configured.");

        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var lockTransaction = await lockConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        bool acquired;
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", DmarcAuthorizationCheckLeaderLockKey);
            acquired = (bool)(await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (!acquired)
        {
            _logger.LogDebug("Another instance already holds the DMARC-authorization-check lock for this cycle; skipping.");
            return;
        }

        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            var staleDomains = await context.Domains
                .Where(d => d.DmarcAuthorizationCheckedUtc == null || d.DmarcAuthorizationCheckedUtc < cutoff)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var anyUpdated = false;
            foreach (var domain in staleDomains)
            {
                try
                {
                    await RunSingleDmarcAuthorizationCheckAsync(domain, dmarcChecker, mailboxAddress, cancellationToken).ConfigureAwait(false);
                    anyUpdated = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DMARC authorization check failed for {Domain}; will retry next cycle.", domain.Name);
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

    /// <summary>Runs an SPF check for every domain whose last check (SpfCheckedUtc) is null or more
    /// than 24 hours old.</summary>
    internal async Task RunSpfCheckCycleAsync(DotMarcDbContext context, ISpfDnsChecker spfChecker, CancellationToken cancellationToken)
    {
        var connectionString = context.Database.GetConnectionString()
            ?? throw new InvalidOperationException("DotMarcDbContext has no connection string configured.");

        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var lockTransaction = await lockConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        bool acquired;
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", SpfCheckLeaderLockKey);
            acquired = (bool)(await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (!acquired)
        {
            _logger.LogDebug("Another instance already holds the SPF-check lock for this cycle; skipping.");
            return;
        }

        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            var staleDomains = await context.Domains
                .Where(d => d.SpfCheckedUtc == null || d.SpfCheckedUtc < cutoff)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var anyUpdated = false;
            foreach (var domain in staleDomains)
            {
                try
                {
                    await RunSingleSpfCheckAsync(domain, spfChecker, cancellationToken).ConfigureAwait(false);
                    anyUpdated = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SPF check failed for {Domain}; will retry next cycle.", domain.Name);
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

    /// <summary>Runs an MX check for every domain whose last check (MxCheckedUtc) is null or more
    /// than 24 hours old.</summary>
    internal async Task RunMxCheckCycleAsync(DotMarcDbContext context, IMxDnsChecker mxChecker, CancellationToken cancellationToken)
    {
        var connectionString = context.Database.GetConnectionString()
            ?? throw new InvalidOperationException("DotMarcDbContext has no connection string configured.");

        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var lockTransaction = await lockConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        bool acquired;
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", MxCheckLeaderLockKey);
            acquired = (bool)(await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (!acquired)
        {
            _logger.LogDebug("Another instance already holds the MX-check lock for this cycle; skipping.");
            return;
        }

        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            var staleDomains = await context.Domains
                .Where(d => d.MxCheckedUtc == null || d.MxCheckedUtc < cutoff)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var anyUpdated = false;
            foreach (var domain in staleDomains)
            {
                try
                {
                    await RunSingleMxCheckAsync(domain, mxChecker, cancellationToken).ConfigureAwait(false);
                    anyUpdated = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MX check failed for {Domain}; will retry next cycle.", domain.Name);
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

    /// <summary>Runs a DKIM check for every domain whose last check (DkimCheckedUtc) is null or
    /// more than 24 hours old. Most domains have no selectors configured, so most iterations of
    /// this loop are the cheap NotConfigured short-circuit inside RunSingleDkimCheckAsync, not an
    /// actual DNS call.</summary>
    internal async Task RunDkimCheckCycleAsync(DotMarcDbContext context, IDkimDnsChecker dkimChecker, CancellationToken cancellationToken)
    {
        var connectionString = context.Database.GetConnectionString()
            ?? throw new InvalidOperationException("DotMarcDbContext has no connection string configured.");

        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var lockTransaction = await lockConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        bool acquired;
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", DkimCheckLeaderLockKey);
            acquired = (bool)(await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (!acquired)
        {
            _logger.LogDebug("Another instance already holds the DKIM-check lock for this cycle; skipping.");
            return;
        }

        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            var staleDomains = await context.Domains
                .Where(d => d.DkimCheckedUtc == null || d.DkimCheckedUtc < cutoff)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var anyUpdated = false;
            foreach (var domain in staleDomains)
            {
                try
                {
                    await RunSingleDkimCheckAsync(domain, dkimChecker, cancellationToken).ConfigureAwait(false);
                    anyUpdated = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DKIM check failed for {Domain}; will retry next cycle.", domain.Name);
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

- [ ] **Step 4: Wire the four new cycles into ExecuteAsync**

In `src/DotMarc/Ingestion/PollingService.cs`'s `ExecuteAsync`, immediately after the existing DMARC-check `try`/`catch` block (the one calling `RunDmarcCheckCycleAsync`, right before the `if (!string.IsNullOrWhiteSpace(_options!.TlsrptMailboxAddress))` block), add:

```csharp
                    try
                    {
                        context.ChangeTracker.Clear();
                        var dmarcChecker = scope.ServiceProvider.GetRequiredService<IDmarcDnsChecker>();
                        await RunDmarcAuthorizationCheckCycleAsync(context, dmarcChecker, _options!.MailboxAddress, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "DMARC authorization check cycle failed; will retry next interval.");
                    }

                    try
                    {
                        context.ChangeTracker.Clear();
                        var spfChecker = scope.ServiceProvider.GetRequiredService<ISpfDnsChecker>();
                        await RunSpfCheckCycleAsync(context, spfChecker, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "SPF check cycle failed; will retry next interval.");
                    }

                    try
                    {
                        context.ChangeTracker.Clear();
                        var mxChecker = scope.ServiceProvider.GetRequiredService<IMxDnsChecker>();
                        await RunMxCheckCycleAsync(context, mxChecker, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "MX check cycle failed; will retry next interval.");
                    }

                    try
                    {
                        context.ChangeTracker.Clear();
                        var dkimChecker = scope.ServiceProvider.GetRequiredService<IDkimDnsChecker>();
                        await RunDkimCheckCycleAsync(context, dkimChecker, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "DKIM check cycle failed; will retry next interval.");
                    }
```

- [ ] **Step 5: Register the three new checkers in Program.cs**

In `src/DotMarc/Program.cs`, immediately after the existing `builder.Services.AddHttpClient<DotMarc.DnsPush.IDmarcAuthorizationTxtLookup, DotMarc.DnsPush.DmarcAuthorizationTxtLookup>(client => { ... });` block, add:

```csharp
builder.Services.AddHttpClient<DotMarc.Dns.ISpfDnsChecker, DotMarc.Dns.SpfDnsChecker>(client =>
{
    client.BaseAddress = new Uri("https://cloudflare-dns.com/");
    client.DefaultRequestHeaders.Add("Accept", "application/dns-json");
});

builder.Services.AddHttpClient<DotMarc.Dns.IMxDnsChecker, DotMarc.Dns.MxDnsChecker>(client =>
{
    client.BaseAddress = new Uri("https://cloudflare-dns.com/");
    client.DefaultRequestHeaders.Add("Accept", "application/dns-json");
});

builder.Services.AddHttpClient<DotMarc.Dns.IDkimDnsChecker, DotMarc.Dns.DkimDnsChecker>(client =>
{
    client.BaseAddress = new Uri("https://cloudflare-dns.com/");
    client.DefaultRequestHeaders.Add("Accept", "application/dns-json");
});
```

(`IDmarcDnsChecker` is already registered elsewhere in `Program.cs` — it doesn't need a new registration, just gains a method.)

**Note on test file targeting**: the existing DMARC/TLSRPT check-cycle tests do NOT live in `PollingServiceTests.cs` — they're in `test/DotMarc.Tests/Ingestion/DmarcCheckCycleTests.cs` (which, despite its name, covers both `RunDmarcCheckCycleAsync` and `RunTlsrptCheckCycleAsync`). MTA-STS's cycle has its own dedicated file, `MtaStsCheckCycleTests.cs`. Steps 6-8 below create one new dedicated test file per new cycle, matching the MTA-STS precedent, and two new fake checkers alongside the existing `FakeDmarcDnsChecker`/`FakeTlsrptDnsChecker`.

- [ ] **Step 6: Create the three new fake checkers**

`test/DotMarc.Tests/Internal/FakeSpfDnsChecker.cs`:
```csharp
using DotMarc.Data;
using DotMarc.Dns;

namespace DotMarc.Tests.Internal;

internal sealed class FakeSpfDnsChecker : ISpfDnsChecker
{
    public SpfCheckResult Result { get; set; } = new(SpfCheckStatus.Ok, null);
    public bool ShouldThrow { get; set; }
    public List<string> CheckedDomains { get; } = [];

    public Task<SpfCheckResult> CheckAsync(string domainName, CancellationToken cancellationToken)
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

`test/DotMarc.Tests/Internal/FakeMxDnsChecker.cs`:
```csharp
using DotMarc.Data;
using DotMarc.Dns;

namespace DotMarc.Tests.Internal;

internal sealed class FakeMxDnsChecker : IMxDnsChecker
{
    public MxCheckResult Result { get; set; } = new(MxCheckStatus.Ok, null);
    public bool ShouldThrow { get; set; }
    public List<string> CheckedDomains { get; } = [];

    public Task<MxCheckResult> CheckAsync(string domainName, CancellationToken cancellationToken)
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

`test/DotMarc.Tests/Internal/FakeDkimDnsChecker.cs`:
```csharp
using DotMarc.Data;
using DotMarc.Dns;

namespace DotMarc.Tests.Internal;

internal sealed class FakeDkimDnsChecker : IDkimDnsChecker
{
    public DkimCheckResult Result { get; set; } = new(DkimCheckStatus.Ok, null);
    public bool ShouldThrow { get; set; }
    public List<string> CheckedDomains { get; } = [];

    public Task<DkimCheckResult> CheckAsync(string domainName, IReadOnlyList<string> selectors, CancellationToken cancellationToken)
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

- [ ] **Step 7: Write the four new cycle test files**

Each follows `DmarcCheckCycleTests.cs`'s exact structure (`[Collection("Postgres")]`, `IAsyncLifetime`, `PostgresContainerFixture`, a `CreateContext()` helper, and `new PollingService(new FakeGraphMailboxClient(), context, NullLogger<PollingService>.Instance)` as the test-only constructor).

`test/DotMarc.Tests/Ingestion/DmarcAuthorizationCheckCycleTests.cs`:
```csharp
using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.Ingestion;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace DotMarc.Tests.Ingestion;

[Collection("Postgres")]
public sealed class DmarcAuthorizationCheckCycleTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DmarcAuthorizationCheckCycleTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task RunDmarcAuthorizationCheckCycleAsync_ChecksADomainNeverCheckedBefore()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        var checker = new FakeDmarcDnsChecker { AuthorizationResult = new DmarcAuthorizationCheckResult(DmarcAuthorizationCheckStatus.Missing, "No TXT record found") };
        var service = CreateService(context);
        await service.RunDmarcAuthorizationCheckCycleAsync(context, checker, "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Contains("contoso.io", checker.AuthorizationCheckedDomains);
        var domain = context.Domains.Single();
        Assert.Equal(DmarcAuthorizationCheckStatus.Missing, domain.DmarcAuthorizationCheckStatus);
        Assert.NotNull(domain.DmarcAuthorizationCheckedUtc);
    }

    [Fact]
    public async Task RunDmarcAuthorizationCheckCycleAsync_SkipsADomainCheckedRecently()
    {
        using var context = CreateContext();
        var recentCheck = DateTimeOffset.UtcNow.AddHours(-1);
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            DmarcAuthorizationCheckStatus = DmarcAuthorizationCheckStatus.Ok,
            DmarcAuthorizationCheckedUtc = recentCheck
        });
        await context.SaveChangesAsync();

        var checker = new FakeDmarcDnsChecker();
        var service = CreateService(context);
        await service.RunDmarcAuthorizationCheckCycleAsync(context, checker, "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Empty(checker.AuthorizationCheckedDomains);
        Assert.Equal(recentCheck, context.Domains.Single().DmarcAuthorizationCheckedUtc);
    }

    [Fact]
    public async Task RunDmarcAuthorizationCheckCycleAsync_LeavesStatusUnchanged_WhenTheCheckItselfThrows()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            DmarcAuthorizationCheckStatus = DmarcAuthorizationCheckStatus.Missing
        });
        await context.SaveChangesAsync();

        var checker = new FakeDmarcDnsChecker { AuthorizationShouldThrow = true };
        var service = CreateService(context);
        await service.RunDmarcAuthorizationCheckCycleAsync(context, checker, "rua.dmarc@mjco.uk", CancellationToken.None);

        using var verify = CreateContext();
        var verifyDomain = verify.Domains.Single();
        Assert.Equal(DmarcAuthorizationCheckStatus.Missing, verifyDomain.DmarcAuthorizationCheckStatus);
        Assert.Null(verifyDomain.DmarcAuthorizationCheckedUtc);
    }

    [Fact]
    public async Task RunDmarcAuthorizationCheckCycleAsync_SkipsEntirely_WhenAnotherInstanceHoldsTheLock()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", PollingService.DmarcAuthorizationCheckLeaderLockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }

        var checker = new FakeDmarcDnsChecker();
        var service = CreateService(context);
        await service.RunDmarcAuthorizationCheckCycleAsync(context, checker, "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Empty(checker.AuthorizationCheckedDomains);

        await lockTransaction.RollbackAsync();
    }
}
```

`test/DotMarc.Tests/Ingestion/SpfCheckCycleTests.cs`:
```csharp
using DotMarc.Data;
using DotMarc.Ingestion;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace DotMarc.Tests.Ingestion;

[Collection("Postgres")]
public sealed class SpfCheckCycleTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public SpfCheckCycleTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task RunSpfCheckCycleAsync_ChecksADomainNeverCheckedBefore()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        var checker = new FakeSpfDnsChecker { Result = new(SpfCheckStatus.MissingRecord, "No SPF record") };
        var service = CreateService(context);
        await service.RunSpfCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Contains("contoso.io", checker.CheckedDomains);
        var domain = context.Domains.Single();
        Assert.Equal(SpfCheckStatus.MissingRecord, domain.SpfCheckStatus);
        Assert.NotNull(domain.SpfCheckedUtc);
    }

    [Fact]
    public async Task RunSpfCheckCycleAsync_SkipsADomainCheckedRecently()
    {
        using var context = CreateContext();
        var recentCheck = DateTimeOffset.UtcNow.AddHours(-1);
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            SpfCheckStatus = SpfCheckStatus.Ok,
            SpfCheckedUtc = recentCheck
        });
        await context.SaveChangesAsync();

        var checker = new FakeSpfDnsChecker();
        var service = CreateService(context);
        await service.RunSpfCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Empty(checker.CheckedDomains);
        Assert.Equal(recentCheck, context.Domains.Single().SpfCheckedUtc);
    }

    [Fact]
    public async Task RunSpfCheckCycleAsync_LeavesStatusUnchanged_WhenTheCheckItselfThrows()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            SpfCheckStatus = SpfCheckStatus.MissingRecord
        });
        await context.SaveChangesAsync();

        var checker = new FakeSpfDnsChecker { ShouldThrow = true };
        var service = CreateService(context);
        await service.RunSpfCheckCycleAsync(context, checker, CancellationToken.None);

        using var verify = CreateContext();
        var verifyDomain = verify.Domains.Single();
        Assert.Equal(SpfCheckStatus.MissingRecord, verifyDomain.SpfCheckStatus);
        Assert.Null(verifyDomain.SpfCheckedUtc);
    }

    [Fact]
    public async Task RunSpfCheckCycleAsync_SkipsEntirely_WhenAnotherInstanceHoldsTheLock()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", PollingService.SpfCheckLeaderLockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }

        var checker = new FakeSpfDnsChecker();
        var service = CreateService(context);
        await service.RunSpfCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Empty(checker.CheckedDomains);

        await lockTransaction.RollbackAsync();
    }
}
```

`test/DotMarc.Tests/Ingestion/MxCheckCycleTests.cs` — identical in shape to `SpfCheckCycleTests.cs` above with every `Spf`/`SPF` replaced by `Mx`/`MX` (`FakeMxDnsChecker`, `MxCheckStatus.MissingRecord`, `Domain.MxCheckStatus`/`MxCheckedUtc`, `PollingService.MxCheckLeaderLockKey`, `RunMxCheckCycleAsync`). Write out all four test methods in full, following that exact substitution — do not abbreviate or reference "the same as SpfCheckCycleTests.cs" in the actual file.

`test/DotMarc.Tests/Ingestion/DkimCheckCycleTests.cs` — same shape as `SpfCheckCycleTests.cs` with `Spf`/`SPF` replaced by `Dkim`/`DKIM` (`FakeDkimDnsChecker`, `DkimCheckStatus.Missing` instead of `MissingRecord` since DKIM has no such member, `Domain.DkimCheckStatus`/`DkimCheckedUtc`, `PollingService.DkimCheckLeaderLockKey`, `RunDkimCheckCycleAsync`), plus one extra test for the opt-in short-circuit:

```csharp
    [Fact]
    public async Task RunDkimCheckCycleAsync_SetsNotConfigured_WhenNoSelectorsAreConfigured()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        var checker = new FakeDkimDnsChecker { Result = new(DkimCheckStatus.Ok, null) };
        var service = CreateService(context);
        await service.RunDkimCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Empty(checker.CheckedDomains); // short-circuited before ever calling the checker
        var domain = context.Domains.Single();
        Assert.Equal(DkimCheckStatus.NotConfigured, domain.DkimCheckStatus);
        Assert.NotNull(domain.DkimCheckedUtc);
    }
```

Write out `MxCheckCycleTests.cs` and `DkimCheckCycleTests.cs` completely — every method body, every using directive — rather than leaving either as a reference to another file's content.

- [ ] **Step 8: Run the new tests to verify they fail, then pass**

```bash
dotnet test DotMarc.sln --filter "FullyQualifiedName~DmarcAuthorizationCheckCycleTests|FullyQualifiedName~SpfCheckCycleTests|FullyQualifiedName~MxCheckCycleTests|FullyQualifiedName~DkimCheckCycleTests"
```
Expected: fails to compile until Steps 1-6 are done, then all 16 pass (4 tests × 4 files).

- [ ] **Step 9: Build and run the full test suite**

```bash
dotnet build DotMarc.sln
dotnet test DotMarc.sln
```
Expected: clean build, all tests pass.

- [ ] **Step 10: Commit**

```bash
git add src/DotMarc/Ingestion/PollingService.cs src/DotMarc/Program.cs test/DotMarc.Tests/Internal/FakeSpfDnsChecker.cs test/DotMarc.Tests/Internal/FakeMxDnsChecker.cs test/DotMarc.Tests/Internal/FakeDkimDnsChecker.cs test/DotMarc.Tests/Ingestion/DmarcAuthorizationCheckCycleTests.cs test/DotMarc.Tests/Ingestion/SpfCheckCycleTests.cs test/DotMarc.Tests/Ingestion/MxCheckCycleTests.cs test/DotMarc.Tests/Ingestion/DkimCheckCycleTests.cs
git commit -m "Wire DMARC authorization, SPF, MX, and DKIM checks into PollingService"
```

---

### Task 7: DomainManagementService.SetDkimSelectorsAsync

**Files:**
- Modify: `src/DotMarc/Data/DomainManagementService.cs`
- Test: `test/DotMarc.Tests/Data/DomainManagementServiceTests.cs`

**Interfaces:**
- Consumes: `Domain.DkimSelectors` (Task 1).
- Produces: `DomainManagementService.SetDkimSelectorsAsync(DotMarcDbContext context, int domainId, List<string> selectors, CancellationToken cancellationToken = default) : Task` — Task 9 (UI dialog) calls this exact signature.

- [ ] **Step 1: Write the failing test**

In `test/DotMarc.Tests/Data/DomainManagementServiceTests.cs`, add (matching the file's existing `CreateContext()`/`AddDomainAsync` setup pattern used by `SetMtaStsConfigAsync`'s tests):

```csharp
    [Fact]
    public async Task SetDkimSelectorsAsync_SavesTheSelectorList()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, "contoso.com", CancellationToken.None);
        var domainId = context.Domains.Single().Id;

        await DomainManagementService.SetDkimSelectorsAsync(context, domainId, ["selector1", "selector2"], CancellationToken.None);

        using var verify = CreateContext();
        var domain = verify.Domains.Single();
        Assert.Equal(["selector1", "selector2"], domain.DkimSelectors);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test DotMarc.sln --filter "FullyQualifiedName~SetDkimSelectorsAsync"
```
Expected: compile error, `SetDkimSelectorsAsync` does not exist.

- [ ] **Step 3: Implement SetDkimSelectorsAsync**

In `src/DotMarc/Data/DomainManagementService.cs`, add after the existing `SetMtaStsConfigAsync` method:

```csharp
    /// <summary>Saves a domain's DKIM selector list from the domain detail page's "Configure DKIM
    /// selectors" dialog. Does not itself trigger a recheck — the dialog's own save handler does
    /// that immediately afterward via PollingService.RunSingleDkimCheckAsync, matching the "enable
    /// MTA-STS" flow's immediate-check-after-save pattern.</summary>
    public static async Task SetDkimSelectorsAsync(DotMarcDbContext context, int domainId, List<string> selectors, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        domain.DkimSelectors = selectors;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test DotMarc.sln --filter "FullyQualifiedName~SetDkimSelectorsAsync"
```
Expected: pass.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Data/DomainManagementService.cs test/DotMarc.Tests/Data/DomainManagementServiceTests.cs
git commit -m "Add DomainManagementService.SetDkimSelectorsAsync"
```

---

### Task 8: DomainHealthCheckRow component and ConfigureDkimSelectorsDialog

**Files:**
- Create: `src/DotMarc/Components/Shared/DomainHealthCheckRow.razor`
- Create: `src/DotMarc/Components/Dialogs/ConfigureDkimSelectorsDialog.razor`

**Interfaces:**
- Produces: `DomainHealthCheckRow` component with parameters `Title` (`string`), `StatusColor` (`MudBlazor.Color`), `StatusLabel` (`string`), `CheckedUtc` (`DateTimeOffset?`), `Detail` (`string?`), and an `ActionContent` (`RenderFragment?`) child-content slot — Task 9 invokes this exact parameter set seven times.
- Produces: `ConfigureDkimSelectorsDialog` — a `MudDialog`-hosted component taking `[Parameter] public List<string> CurrentSelectors { get; set; }`, closing with `DialogResult.Ok(List<string> newSelectors)` on save or `MudDialog.Cancel()` — Task 9 shows it via `IDialogService.ShowAsync<ConfigureDkimSelectorsDialog>` and reads the result.

- [ ] **Step 1: Create DomainHealthCheckRow.razor**

`src/DotMarc/Components/Shared/DomainHealthCheckRow.razor`:
```razor
@* One row of the domain detail page's Overview "Domain health" checklist. A CSS grid, not a
   MudTable row: every check's action differs (push button, recheck button, tab link, configure
   button, or nothing), so a real MudTable's homogeneous RowTemplate would fight this more than
   help — see the design spec's UI changes section. *@
<div class="dotmarc-health-row" style="display:grid; grid-template-columns: 220px 160px 180px 1fr; gap:8px; align-items:center; padding:8px 0; border-bottom:1px solid var(--mud-palette-lines-default);">
    <MudText Typo="Typo.body1">@Title</MudText>
    <MudChip T="string" Color="@StatusColor" Size="Size.Small">@StatusLabel</MudChip>
    <MudText Typo="Typo.caption">@(CheckedUtc is { } checkedUtc ? $"Checked: {checkedUtc:yyyy-MM-dd HH:mm:ss}" : "Not checked yet")</MudText>
    <div class="d-flex align-center flex-wrap" style="gap:8px;">
        @if (!string.IsNullOrWhiteSpace(Detail))
        {
            <MudText Typo="Typo.body2">@Detail</MudText>
        }
        @if (ActionContent is not null)
        {
            @ActionContent
        }
    </div>
</div>

@code {
    [Parameter, EditorRequired] public string Title { get; set; } = "";
    [Parameter, EditorRequired] public Color StatusColor { get; set; }
    [Parameter, EditorRequired] public string StatusLabel { get; set; } = "";
    [Parameter] public DateTimeOffset? CheckedUtc { get; set; }
    [Parameter] public string? Detail { get; set; }
    [Parameter] public RenderFragment? ActionContent { get; set; }
}
```

- [ ] **Step 2: Create ConfigureDkimSelectorsDialog.razor**

`src/DotMarc/Components/Dialogs/ConfigureDkimSelectorsDialog.razor`:
```razor
@* src/DotMarc/Components/Dialogs/ConfigureDkimSelectorsDialog.razor *@
<MudDialog>
    <TitleContent>Configure DKIM selectors</TitleContent>
    <DialogContent>
        <MudText Typo="Typo.body2" Class="mb-2">
            One selector per line (e.g. <code>selector1</code>, <code>google</code>). dotMARC checks
            <code>&lt;selector&gt;._domainkey.@DomainName</code> for each one you list here.
        </MudText>
        <MudTextField @bind-Value="_selectorsText" Label="Selectors" Lines="4" />
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        <MudButton Color="Color.Primary" Variant="Variant.Filled" OnClick="Save">Save</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public string DomainName { get; set; } = "";
    [Parameter] public List<string> CurrentSelectors { get; set; } = [];

    private string _selectorsText = "";

    protected override void OnInitialized() => _selectorsText = string.Join('\n', CurrentSelectors);

    private void Save()
    {
        var selectors = _selectorsText
            .Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        MudDialog.Close(DialogResult.Ok(selectors));
    }

    private void Cancel() => MudDialog.Cancel();
}
```

- [ ] **Step 3: Build**

```bash
dotnet build DotMarc.sln
```
Expected: clean build (both components are unreferenced by anything yet, so this only confirms they compile in isolation).

- [ ] **Step 4: Commit**

```bash
git add src/DotMarc/Components/Shared/DomainHealthCheckRow.razor src/DotMarc/Components/Dialogs/ConfigureDkimSelectorsDialog.razor
git commit -m "Add DomainHealthCheckRow component and DKIM selector configuration dialog"
```

---

### Task 9: DomainDetail.razor — consolidated Overview health checklist

**Files:**
- Modify: `src/DotMarc/Components/Pages/DomainDetail.razor`

**Interfaces:**
- Consumes: `DomainHealthCheckRow` and `ConfigureDkimSelectorsDialog` (Task 8), `PollingService.RunSingleDmarcAuthorizationCheckAsync`/`RunSingleSpfCheckAsync`/`RunSingleMxCheckAsync`/`RunSingleDkimCheckAsync` (Task 6), `DomainManagementService.SetDkimSelectorsAsync` (Task 7), `DmarcAuthorizationStatusPresentation`/`SpfStatusPresentation`/`MxStatusPresentation`/`DkimStatusPresentation` (Task 1), `ISpfDnsChecker`/`IMxDnsChecker`/`IDkimDnsChecker` (Tasks 3-5).

- [ ] **Step 1: Add the new service injections**

In `src/DotMarc/Components/Pages/DomainDetail.razor`, after the existing `@inject ITlsrptDnsChecker TlsrptDnsChecker` line, add:

```razor
@inject ISpfDnsChecker SpfDnsChecker
@inject IMxDnsChecker MxDnsChecker
@inject IDkimDnsChecker DkimDnsChecker
```

- [ ] **Step 2: Replace the Overview tab's two status panels with the consolidated checklist**

Replace the entire block from `<MudPaper Class="pa-4 mt-4" Elevation="1">` (the DMARC panel, currently starting at line 53) through the closing `}` of the TLSRPT panel's `@if (!string.IsNullOrWhiteSpace(GraphOptions.Value.TlsrptMailboxAddress))` block (currently ending at line 162) with:

```razor
            <MudPaper Class="pa-4 mt-4" Elevation="1">
                <MudText Typo="Typo.subtitle1" Class="mb-2">Domain health</MudText>

                <DomainHealthCheckRow Title="DMARC record"
                                      StatusColor="@DmarcStatusPresentation.GetColor(_domain.DmarcCheckStatus)"
                                      StatusLabel="@DmarcStatusPresentation.GetLabel(_domain.DmarcCheckStatus)"
                                      CheckedUtc="_domain.DmarcCheckedUtc"
                                      Detail="_domain.DmarcCheckDetail">
                    <ActionContent>
                        <AuthorizeView Policy="DomainsEdit" Context="dmarcRecheckAuthState">
                            <Authorized>
                                @if (_isRecheckingDmarc)
                                {
                                    <MudProgressCircular Size="Size.Small" Indeterminate="true" />
                                }
                                else
                                {
                                    <MudIconButton Icon="@Icons.Material.Filled.Refresh" Size="Size.Small" OnClick="RecheckDmarcAsync" title="Recheck now" />
                                }
                                @if (_domain.DmarcCheckStatus is DmarcCheckStatus.MissingOwnRecord or DmarcCheckStatus.Misconfigured)
                                {
                                    @if (_isPushingDmarcRecord)
                                    {
                                        <MudProgressCircular Size="Size.Small" Indeterminate="true" />
                                    }
                                    else
                                    {
                                        <MudButton Variant="Variant.Text" Color="Color.Primary" StartIcon="@Icons.Material.Filled.CloudSync"
                                                   OnClick="PushDmarcRecordAsync">Push via your DNS provider</MudButton>
                                    }
                                }
                            </Authorized>
                        </AuthorizeView>
                    </ActionContent>
                </DomainHealthCheckRow>

                <DomainHealthCheckRow Title="DMARC authorization record"
                                      StatusColor="@DmarcAuthorizationStatusPresentation.GetColor(_domain.DmarcAuthorizationCheckStatus)"
                                      StatusLabel="@DmarcAuthorizationStatusPresentation.GetLabel(_domain.DmarcAuthorizationCheckStatus)"
                                      CheckedUtc="_domain.DmarcAuthorizationCheckedUtc"
                                      Detail="_domain.DmarcAuthorizationCheckDetail">
                    <ActionContent>
                        <AuthorizeView Policy="DomainsEdit" Context="dmarcAuthRecheckAuthState">
                            <Authorized>
                                @if (_isRecheckingDmarcAuth)
                                {
                                    <MudProgressCircular Size="Size.Small" Indeterminate="true" />
                                }
                                else
                                {
                                    <MudIconButton Icon="@Icons.Material.Filled.Refresh" Size="Size.Small" OnClick="RecheckDmarcAuthorizationAsync" title="Recheck now" />
                                }
                                @if (_domain.DmarcAuthorizationCheckStatus == DmarcAuthorizationCheckStatus.Missing)
                                {
                                    @if (_isPushingDmarcAuthRecord)
                                    {
                                        <MudProgressCircular Size="Size.Small" Indeterminate="true" />
                                    }
                                    else
                                    {
                                        <MudButton Variant="Variant.Text" Color="Color.Primary" StartIcon="@Icons.Material.Filled.CloudSync"
                                                   OnClick="PushDmarcAuthorizationRecordAsync">Push authorization record via your DNS provider</MudButton>
                                    }
                                }
                            </Authorized>
                        </AuthorizeView>
                    </ActionContent>
                </DomainHealthCheckRow>

                @if (!string.IsNullOrWhiteSpace(GraphOptions.Value.TlsrptMailboxAddress))
                {
                    <DomainHealthCheckRow Title="TLS reporting record"
                                          StatusColor="@TlsrptStatusPresentation.GetColor(_domain.TlsrptCheckStatus)"
                                          StatusLabel="@TlsrptStatusPresentation.GetLabel(_domain.TlsrptCheckStatus)"
                                          CheckedUtc="_domain.TlsrptCheckedUtc"
                                          Detail="_domain.TlsrptCheckDetail">
                        <ActionContent>
                            <AuthorizeView Policy="DomainsEdit" Context="tlsrptRecheckAuthState">
                                <Authorized>
                                    @if (_isRecheckingTlsrpt)
                                    {
                                        <MudProgressCircular Size="Size.Small" Indeterminate="true" />
                                    }
                                    else
                                    {
                                        <MudIconButton Icon="@Icons.Material.Filled.Refresh" Size="Size.Small" OnClick="RecheckTlsrptAsync" title="Recheck now" />
                                    }
                                    @if (_domain.TlsrptCheckStatus is TlsrptCheckStatus.MissingOwnRecord or TlsrptCheckStatus.Misconfigured)
                                    {
                                        <MudButton Variant="Variant.Text" Color="Color.Primary" StartIcon="@Icons.Material.Filled.CloudSync"
                                                   OnClick="PushTlsrptRecordAsync">Push via your DNS provider</MudButton>
                                    }
                                </Authorized>
                            </AuthorizeView>
                        </ActionContent>
                    </DomainHealthCheckRow>
                }

                <DomainHealthCheckRow Title="MTA-STS"
                                      StatusColor="@MtaStsStatusPresentation.GetColor(_domain.MtaStsStatus)"
                                      StatusLabel="@MtaStsStatusPresentation.GetLabel(_domain.MtaStsStatus)"
                                      CheckedUtc="_domain.MtaStsCheckedUtc"
                                      Detail="_domain.MtaStsCheckDetail">
                    <ActionContent>
                        <MudLink OnClick="@(() => NavigateToTabAsync("mta-sts"))" Style="cursor:pointer">View details</MudLink>
                    </ActionContent>
                </DomainHealthCheckRow>

                <DomainHealthCheckRow Title="SPF"
                                      StatusColor="@SpfStatusPresentation.GetColor(_domain.SpfCheckStatus)"
                                      StatusLabel="@SpfStatusPresentation.GetLabel(_domain.SpfCheckStatus)"
                                      CheckedUtc="_domain.SpfCheckedUtc"
                                      Detail="_domain.SpfCheckDetail">
                    <ActionContent>
                        <AuthorizeView Policy="DomainsEdit" Context="spfRecheckAuthState">
                            <Authorized>
                                @if (_isRecheckingSpf)
                                {
                                    <MudProgressCircular Size="Size.Small" Indeterminate="true" />
                                }
                                else
                                {
                                    <MudIconButton Icon="@Icons.Material.Filled.Refresh" Size="Size.Small" OnClick="RecheckSpfAsync" title="Recheck now" />
                                }
                            </Authorized>
                        </AuthorizeView>
                    </ActionContent>
                </DomainHealthCheckRow>

                <DomainHealthCheckRow Title="MX"
                                      StatusColor="@MxStatusPresentation.GetColor(_domain.MxCheckStatus)"
                                      StatusLabel="@MxStatusPresentation.GetLabel(_domain.MxCheckStatus)"
                                      CheckedUtc="_domain.MxCheckedUtc"
                                      Detail="_domain.MxCheckDetail">
                    <ActionContent>
                        <AuthorizeView Policy="DomainsEdit" Context="mxRecheckAuthState">
                            <Authorized>
                                @if (_isRecheckingMx)
                                {
                                    <MudProgressCircular Size="Size.Small" Indeterminate="true" />
                                }
                                else
                                {
                                    <MudIconButton Icon="@Icons.Material.Filled.Refresh" Size="Size.Small" OnClick="RecheckMxAsync" title="Recheck now" />
                                }
                            </Authorized>
                        </AuthorizeView>
                    </ActionContent>
                </DomainHealthCheckRow>

                <DomainHealthCheckRow Title="DKIM"
                                      StatusColor="@DkimStatusPresentation.GetColor(_domain.DkimCheckStatus)"
                                      StatusLabel="@DkimStatusPresentation.GetLabel(_domain.DkimCheckStatus)"
                                      CheckedUtc="_domain.DkimCheckedUtc"
                                      Detail="_domain.DkimCheckDetail">
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
```

- [ ] **Step 3: Add the new private fields**

In the `@code` block, after the existing `private bool _isRecheckingTlsrpt;` line, add:

```csharp
    private bool _isRecheckingDmarcAuth;
    private bool _isRecheckingSpf;
    private bool _isRecheckingMx;
    private bool _isRecheckingDkim;
```

- [ ] **Step 4: Add a shared tab-navigation helper**

The MTA-STS row's "View details" link needs a way to jump to the MTA-STS tab. Add this new private method right after `OnActiveTabIndexChangedAsync`:

```csharp
    private Task NavigateToTabAsync(string slug)
    {
        var index = Array.IndexOf(TabSlugs, slug);
        return OnActiveTabIndexChangedAsync(index >= 0 ? index : 0);
    }
```

- [ ] **Step 5: Add RecheckDmarcAuthorizationAsync**

Immediately after the existing `RecheckDmarcAsync` method, add:

```csharp
    /// <summary>DMARC-authorization counterpart to RecheckDmarcAsync — see its remarks.</summary>
    private async Task RecheckDmarcAuthorizationAsync()
    {
        _isRecheckingDmarcAuth = true;
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            var domain = await db.Domains.SingleAsync(d => d.Id == _domain!.Id);
            try
            {
                await PollingService.RunSingleDmarcAuthorizationCheckAsync(domain, DmarcDnsChecker, GraphOptions.Value.MailboxAddress, CancellationToken.None);
            }
            catch (Exception)
            {
                Snackbar.Add($"Failed to check {DomainName}'s DMARC authorization record. Try again.", Severity.Error);
                return;
            }
            await db.SaveChangesAsync();

            _domain!.DmarcAuthorizationCheckStatus = domain.DmarcAuthorizationCheckStatus;
            _domain.DmarcAuthorizationCheckedUtc = domain.DmarcAuthorizationCheckedUtc;
            _domain.DmarcAuthorizationCheckDetail = domain.DmarcAuthorizationCheckDetail;
        }
        finally
        {
            _isRecheckingDmarcAuth = false;
        }
    }
```

- [ ] **Step 6: Add RecheckSpfAsync, RecheckMxAsync, RecheckDkimAsync**

Immediately after `RecheckTlsrptAsync`, add:

```csharp
    /// <summary>SPF counterpart to RecheckDmarcAsync — see its remarks.</summary>
    private async Task RecheckSpfAsync()
    {
        _isRecheckingSpf = true;
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            var domain = await db.Domains.SingleAsync(d => d.Id == _domain!.Id);
            try
            {
                await PollingService.RunSingleSpfCheckAsync(domain, SpfDnsChecker, CancellationToken.None);
            }
            catch (Exception)
            {
                Snackbar.Add($"Failed to check {DomainName}'s SPF record. Try again.", Severity.Error);
                return;
            }
            await db.SaveChangesAsync();

            _domain!.SpfCheckStatus = domain.SpfCheckStatus;
            _domain.SpfCheckedUtc = domain.SpfCheckedUtc;
            _domain.SpfCheckDetail = domain.SpfCheckDetail;
        }
        finally
        {
            _isRecheckingSpf = false;
        }
    }

    /// <summary>MX counterpart to RecheckDmarcAsync — see its remarks.</summary>
    private async Task RecheckMxAsync()
    {
        _isRecheckingMx = true;
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            var domain = await db.Domains.SingleAsync(d => d.Id == _domain!.Id);
            try
            {
                await PollingService.RunSingleMxCheckAsync(domain, MxDnsChecker, CancellationToken.None);
            }
            catch (Exception)
            {
                Snackbar.Add($"Failed to check {DomainName}'s MX record. Try again.", Severity.Error);
                return;
            }
            await db.SaveChangesAsync();

            _domain!.MxCheckStatus = domain.MxCheckStatus;
            _domain.MxCheckedUtc = domain.MxCheckedUtc;
            _domain.MxCheckDetail = domain.MxCheckDetail;
        }
        finally
        {
            _isRecheckingMx = false;
        }
    }

    /// <summary>DKIM counterpart to RecheckDmarcAsync — see its remarks.</summary>
    private async Task RecheckDkimAsync()
    {
        _isRecheckingDkim = true;
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            var domain = await db.Domains.SingleAsync(d => d.Id == _domain!.Id);
            try
            {
                await PollingService.RunSingleDkimCheckAsync(domain, DkimDnsChecker, CancellationToken.None);
            }
            catch (Exception)
            {
                Snackbar.Add($"Failed to check {DomainName}'s DKIM record. Try again.", Severity.Error);
                return;
            }
            await db.SaveChangesAsync();

            _domain!.DkimCheckStatus = domain.DkimCheckStatus;
            _domain.DkimCheckedUtc = domain.DkimCheckedUtc;
            _domain.DkimCheckDetail = domain.DkimCheckDetail;
        }
        finally
        {
            _isRecheckingDkim = false;
        }
    }
```

- [ ] **Step 7: Add OpenDkimSelectorsDialogAsync**

Immediately after `RecheckDkimAsync`, add:

```csharp
    private async Task OpenDkimSelectorsDialogAsync()
    {
        var parameters = new DialogParameters<ConfigureDkimSelectorsDialog>
        {
            { x => x.DomainName, DomainName },
            { x => x.CurrentSelectors, _domain!.DkimSelectors }
        };
        var dialogRef = await DialogService.ShowAsync<ConfigureDkimSelectorsDialog>("Configure DKIM selectors", parameters);
        var result = await dialogRef.Result;
        if (result is null || result.Canceled)
        {
            return;
        }

        var selectors = (List<string>)result.Data!;
        await using var db = await DbFactory.CreateDbContextAsync();
        await DomainManagementService.SetDkimSelectorsAsync(db, _domain!.Id, selectors, CancellationToken.None);
        _domain.DkimSelectors = selectors;

        await RecheckDkimAsync();
    }
```

- [ ] **Step 8: Build**

```bash
dotnet build DotMarc.sln
```
Expected: clean build.

- [ ] **Step 9: Manual verification**

Start the app locally against demo data (see this repo's existing pattern: `Demo__Enabled=true ASPNETCORE_ENVIRONMENT=Development dotnet run` from `src/DotMarc/`), sign in via `/demo/sign-in/admin`, and open a domain's Overview tab. Confirm: all seven rows render, the DMARC/DMARC-authorization/TLSRPT rows still show their push buttons under the same conditions as before, MTA-STS's "View details" link switches to the MTA-STS tab, SPF/MX show a recheck button but no push button, and DKIM shows "Not configured" with a "Configure selectors" button until selectors are set (demo data won't have any yet until Task 10 seeds them).

- [ ] **Step 10: Run the full test suite**

```bash
dotnet test DotMarc.sln
```
Expected: all pass (no test in this suite renders `DomainDetail.razor`'s markup directly, so this mainly confirms nothing else broke).

- [ ] **Step 11: Commit**

```bash
git add src/DotMarc/Components/Pages/DomainDetail.razor
git commit -m "Replace Overview tab's status panels with a consolidated domain health checklist"
```

---

### Task 10: Demo data coverage for the new checks

**Files:**
- Modify: `src/DotMarc/Demo/DemoDataset.cs`
- Modify: `src/DotMarc/Demo/DemoDataGenerator.cs`
- Modify: `src/DotMarc/Demo/DemoDataSeeder.cs`
- Test: `test/DotMarc.Tests/Demo/DemoDataGeneratorTests.cs` (if it asserts on `DemoDomainSeed`'s field list or a specific domain's field values, update it to match; otherwise no change needed there)

**Interfaces:**
- Consumes: `DmarcAuthorizationCheckStatus`, `SpfCheckStatus`, `MxCheckStatus`, `DkimCheckStatus` (Task 1).

- [ ] **Step 1: Extend DemoDomainSeed with the new fields**

In `src/DotMarc/Demo/DemoDataset.cs`, add to the end of the `DemoDomainSeed` record's parameter list (after `List<DemoTlsrptReportSeed> TlsrptReports`):

```csharp
    DmarcAuthorizationCheckStatus DmarcAuthorizationCheckStatus = DmarcAuthorizationCheckStatus.NotApplicable,
    string? DmarcAuthorizationCheckDetail = null,
    SpfCheckStatus SpfCheckStatus = SpfCheckStatus.Ok,
    string? SpfCheckDetail = null,
    MxCheckStatus MxCheckStatus = MxCheckStatus.Ok,
    string? MxCheckDetail = null,
    List<string>? DkimSelectors = null,
    DkimCheckStatus DkimCheckStatus = DkimCheckStatus.NotConfigured,
    string? DkimCheckDetail = null);
```

(Note the trailing `);` — these become the record's final parameters, all with defaults so every existing `new DemoDomainSeed(...)` call site — there are none outside `DemoDataGenerator.cs`, which Step 2 updates — keeps compiling. Positional-record parameters with defaults must come after all parameters without defaults, which they do here since they're appended at the end.)

- [ ] **Step 2: Update DemoDataGenerator's BuildDomain to accept and pass through the new statuses**

In `src/DotMarc/Demo/DemoDataGenerator.cs`, add new optional parameters to `BuildDomain`'s signature (after the existing `int[]? tlsrptDailyFailedSessions = null` parameter):

```csharp
        DmarcAuthorizationCheckStatus dmarcAuthorizationCheckStatus = DmarcAuthorizationCheckStatus.NotApplicable,
        string? dmarcAuthorizationCheckDetail = null,
        SpfCheckStatus spfCheckStatus = SpfCheckStatus.Ok,
        string? spfCheckDetail = null,
        MxCheckStatus mxCheckStatus = MxCheckStatus.Ok,
        string? mxCheckDetail = null,
        List<string>? dkimSelectors = null,
        DkimCheckStatus dkimCheckStatus = DkimCheckStatus.NotConfigured,
        string? dkimCheckDetail = null)
```

Then in the `return new DemoDomainSeed(...)` at the end of `BuildDomain`, add the new arguments (after the existing `TlsrptReports: ...` line):

```csharp
            DmarcAuthorizationCheckStatus: dmarcAuthorizationCheckStatus,
            DmarcAuthorizationCheckDetail: dmarcAuthorizationCheckDetail,
            SpfCheckStatus: spfCheckStatus,
            SpfCheckDetail: spfCheckDetail,
            MxCheckStatus: mxCheckStatus,
            MxCheckDetail: mxCheckDetail,
            DkimSelectors: dkimSelectors ?? [],
            DkimCheckStatus: dkimCheckStatus,
            DkimCheckDetail: dkimCheckDetail);
```

- [ ] **Step 3: Give at least one demo domain each new failure mode**

Still in `DemoDataGenerator.cs`, update these two existing `BuildDomain(...)` call sites in `Generate`:

Replace the `driftwood-media.example` entry (currently using `status: DmarcCheckStatus.MissingAuthorizationRecord`) with:
```csharp
            BuildDomain(random, nowUtc, sortOrder: 5, name: "driftwood-media.example", groupName: "Driftwood Media",
                orgs: ["yahoo.com", "protonmail.com"], passRateForDay: _ => 0.85,
                status: DmarcCheckStatus.Ok, detail: null, daysOfHistory: HistoryDays,
                dmarcAuthorizationCheckStatus: DmarcAuthorizationCheckStatus.Missing,
                dmarcAuthorizationCheckDetail: "No TXT record found at driftwood-media.example._report._dmarc.nova-msp.example"),
```

Replace the `cobalt-freight.example` entry with one that also demonstrates the new SPF/MX/DKIM failure modes (add these arguments to its existing call, keeping everything else unchanged):
```csharp
                spfCheckStatus: SpfCheckStatus.MultipleRecords,
                spfCheckDetail: "cobalt-freight.example has 2 SPF records — RFC 7208 requires exactly one",
                mxCheckStatus: MxCheckStatus.UnresolvableTarget,
                mxCheckDetail: "MX target(s) do not resolve: mail.cobalt-freight.example",
                dkimSelectors: ["selector1"],
                dkimCheckStatus: DkimCheckStatus.Missing,
                dkimCheckDetail: "No DKIM record found for selector(s): selector1"),
```

And give `brightline-legal.example` an SPF-missing example (add to its existing call):
```csharp
                spfCheckStatus: SpfCheckStatus.MissingRecord,
                spfCheckDetail: "No SPF (v=spf1) TXT record found at brightline-legal.example"),
```

Every other existing `BuildDomain(...)` call site is left unchanged — it picks up the new parameters' defaults (`DmarcAuthorizationCheckStatus.NotApplicable`, `SpfCheckStatus.Ok`, `MxCheckStatus.Ok`, `DkimCheckStatus.NotConfigured`), which is a reasonable "everything's fine" baseline for domains this feature isn't specifically illustrating.

- [ ] **Step 4: Seed the new fields in DemoDataSeeder**

In `src/DotMarc/Demo/DemoDataSeeder.cs`, in the `Domain` object-initializer block that currently ends with `TlsrptCheckDetail = domainSeed.TlsrptCheckDetail`, add (before the closing `};` — check the exact current closing syntax and match it):

```csharp
                DmarcAuthorizationCheckStatus = domainSeed.DmarcAuthorizationCheckStatus,
                DmarcAuthorizationCheckedUtc = domainSeed.DmarcAuthorizationCheckStatus == DmarcAuthorizationCheckStatus.NotChecked ? null : domainSeed.FirstSeenUtc,
                DmarcAuthorizationCheckDetail = domainSeed.DmarcAuthorizationCheckDetail,
                SpfCheckStatus = domainSeed.SpfCheckStatus,
                SpfCheckedUtc = domainSeed.SpfCheckStatus == SpfCheckStatus.NotChecked ? null : domainSeed.FirstSeenUtc,
                SpfCheckDetail = domainSeed.SpfCheckDetail,
                MxCheckStatus = domainSeed.MxCheckStatus,
                MxCheckedUtc = domainSeed.MxCheckStatus == MxCheckStatus.NotChecked ? null : domainSeed.FirstSeenUtc,
                MxCheckDetail = domainSeed.MxCheckDetail,
                DkimSelectors = domainSeed.DkimSelectors,
                DkimCheckStatus = domainSeed.DkimCheckStatus,
                DkimCheckedUtc = domainSeed.DkimCheckStatus == DkimCheckStatus.NotConfigured ? null : domainSeed.FirstSeenUtc,
                DkimCheckDetail = domainSeed.DkimCheckDetail
```

(Matches the file's existing convention, visible on the `DmarcCheckedUtc`/`MtaStsCheckedUtc` lines just above: the checked-timestamp is null exactly when the status is still at its "never checked" default, otherwise `domainSeed.FirstSeenUtc`.)

- [ ] **Step 5: Build and run the full test suite**

```bash
dotnet build DotMarc.sln
dotnet test DotMarc.sln
```
Expected: clean build. If `DemoDataGeneratorTests.cs` has a test asserting the exact shape/count of `DemoDomainSeed`'s fields or a specific domain's prior field values (e.g. asserting `driftwood-media.example`'s old `DmarcCheckStatus.MissingAuthorizationRecord`), update that specific assertion to match the new field split from Step 3 — read the test file first to find any such assertion before assuming none exist.

- [ ] **Step 6: Manual verification**

Start the app locally in demo mode (`Demo__Enabled=true ASPNETCORE_ENVIRONMENT=Development dotnet run` from `src/DotMarc/`), sign in via `/demo/sign-in/admin`, and check: `driftwood-media.example`'s Overview tab shows "DMARC record: OK" and "DMARC authorization record: Missing authorization" as two separate rows; `cobalt-freight.example` shows SPF/MX/DKIM failures; `brightline-legal.example` shows a missing SPF record; every other domain shows SPF/MX as OK and DKIM as "Not configured".

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Demo/DemoDataset.cs src/DotMarc/Demo/DemoDataGenerator.cs src/DotMarc/Demo/DemoDataSeeder.cs test/DotMarc.Tests/Demo/DemoDataGeneratorTests.cs
git commit -m "Extend demo data to cover DMARC authorization, SPF, MX, and DKIM checks"
```

---

## Final Verification

After all 10 tasks are complete:

```bash
dotnet build DotMarc.sln
dotnet test DotMarc.sln
```

Expected: clean build (0 warnings, 0 errors), all tests pass. Then manually verify the full Overview checklist one more time end-to-end against demo data (Task 9 Step 9 + Task 10 Step 6's checks together), confirming no row regressed and every push/recheck/configure action still works.
