# Null-Routed Domain Detection & Mail Service Detection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Detect RFC 7505 null MX and SPF's `v=spf1 -all` "no senders" convention as first-class check states, use the SPF one to automatically classify a domain as "null-routed" and invert its alerting (suppress missing-report, raise unexpected-activity), surface that status on the Dashboard and domain detail page, detect and badge known mail services from MX/SPF, and pre-fill DKIM selector suggestions from the detected inbox provider.

**Architecture:** Extend the two existing checkers (`MxDnsChecker`, `SpfDnsChecker`) with one new terminal status each rather than a new concept - "null-routed" is never stored, it's computed inline from `Domain.SpfCheckStatus` everywhere it's needed (`AlertingService`, `DashboardSummary`, nowhere else). Mail service detection is a brand-new, independent DNS-over-HTTPS checker (`MailServiceDetector`) following the same "own raw query, no shared abstraction" convention as every other checker in this codebase - it does not reuse `MxDnsChecker`/`SpfDnsChecker`, matching the precedent that `MxDnsChecker` itself doesn't reuse `IMxHostsLookup`. DKIM selector suggestions come from a small pure lookup table (`DkimSelectorSuggestions`), shared by name with `MailServiceDetector`'s provider strings but implemented independently since the two have different consumers (a live detector vs. a dialog pre-fill).

**Tech Stack:** .NET 10, Blazor Server, EF Core + Npgsql, MudBlazor 9.8.0, xUnit + Testcontainers (Postgres).

**Spec:** `docs/superpowers/specs/2026-09-07-null-routed-domains-and-mail-service-detection-design.md`

## Global Constraints

- No new persisted field for "null-routed." `IsNullRouted` is always the inline expression `SpfCheckStatus == SpfCheckStatus.NullSpf`, computed wherever needed - never stored as a second value that could drift out of sync.
- Both new enum members (`MxCheckStatus.NullMx`, `SpfCheckStatus.NullSpf`) require **no EF Core migration** - `HasConversion<string>()` is already configured for both columns in `DotMarcDbContext.cs`, so a new enum member is just a new string value the existing conversion already handles. Do not run `dotnet ef migrations add`.
- Every new DNS-over-HTTPS checker is its own small, independent class querying `https://cloudflare-dns.com/` directly - no shared base class or generic DNS-checking abstraction. `MailServiceDetector` does its own raw MX and TXT queries rather than reusing `MxDnsChecker`/`SpfDnsChecker`.
- Mail service detection badges are informational only - no push/remediation action attached.
- DKIM selector auto-suggestion never overwrites an already-configured selector list, and the pre-filled text stays fully editable/clearable before saving - never applied without the admin's action.
- No manual "mark as null-routed" toggle anywhere in the UI. Classification is fully automatic from `SpfCheckStatus`.
- Recheck buttons and any policy gating touched by this plan keep the exact same `DomainsEdit` policy already used by the SPF/MX rows in `DomainDetail.razor` - no new permission is introduced.

---

### Task 1: Data model - `NullMx` and `NullSpf` enum members

**Files:**
- Modify: `src/DotMarc/Data/MxCheckStatus.cs`
- Modify: `src/DotMarc/Data/SpfCheckStatus.cs`

**Interfaces:**
- Produces: `MxCheckStatus.NullMx` (new member, inserted immediately after `Ok`), `SpfCheckStatus.NullSpf` (new member, inserted immediately after `Ok`) - every later task's checker/presentation/alerting code uses these exact names.

- [ ] **Step 1: Add `NullMx` to `MxCheckStatus`**

Replace the full contents of `src/DotMarc/Data/MxCheckStatus.cs`:
```csharp
namespace DotMarc.Data;

/// <summary>The result of the most recent MX DNS check for a Domain - see
/// DotMarc.Dns.MxDnsChecker. An explicit RFC 7505 null MX ("0 .") is NullMx - an intentional "this
/// domain sends but does not receive mail" policy, not a failure, but distinguishable from a normal
/// passing check since it also drives the domain's "null-routed" classification (see
/// DotMarc.Notifications.AlertingService and DashboardSummary, both of which key off
/// SpfCheckStatus.NullSpf specifically - NullMx alone does not flip alerting). NotChecked is listed
/// first so it is the enum's (and the database column's) default value.</summary>
public enum MxCheckStatus
{
    NotChecked,
    Ok,
    NullMx,
    MissingRecord,
    UnresolvableTarget
}
```

- [ ] **Step 2: Add `NullSpf` to `SpfCheckStatus`**

Replace the full contents of `src/DotMarc/Data/SpfCheckStatus.cs`:
```csharp
namespace DotMarc.Data;

/// <summary>The result of the most recent SPF DNS check for a Domain - see
/// DotMarc.Dns.SpfDnsChecker. Only checks presence, record uniqueness, the v=spf1 prefix, and the
/// "no senders authorized" null pattern (v=spf1 -all, exactly); does not validate the 10-DNS-lookup
/// limit (RFC 7208) or the mechanism chain. NullSpf drives the domain's "null-routed"
/// classification everywhere it's used (AlertingService, DashboardSummary) - it is the single
/// source of truth for that concept; there is no separate stored flag. NotChecked is listed first
/// so it is the enum's (and the database column's) default value.</summary>
public enum SpfCheckStatus
{
    NotChecked,
    Ok,
    NullSpf,
    MissingRecord,
    MultipleRecords,
    Misconfigured
}
```

- [ ] **Step 3: Build and run the full test suite to confirm nothing else broke**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds; test run may show 1-2 pre-existing failures that later tasks will fix (`MxDnsCheckerTests.CheckAsync_ReturnsOk_WhenNullMxIsPublished` still asserts the old `Ok` status against unchanged `MxDnsChecker` code, so it still passes at this point - Task 2 is what changes the checker itself). No new compile errors.

- [ ] **Step 4: Commit**

```bash
git add src/DotMarc/Data/MxCheckStatus.cs src/DotMarc/Data/SpfCheckStatus.cs
git commit -m "Add NullMx and NullSpf check statuses"
```

---

### Task 2: `MxDnsChecker` - return `NullMx` for an explicit null MX

**Files:**
- Modify: `src/DotMarc/Dns/MxDnsChecker.cs`
- Modify: `src/DotMarc/Reporting/MxStatusPresentation.cs`
- Test: `test/DotMarc.Tests/Dns/MxDnsCheckerTests.cs`

**Interfaces:**
- Consumes: `MxCheckStatus.NullMx` (Task 1).
- Produces: `MxDnsChecker.CheckAsync` now returns `MxCheckResult(MxCheckStatus.NullMx, "Explicit null MX (RFC 7505) - this domain intentionally does not accept mail.")` for a standalone null MX (unchanged detail text). `MxStatusPresentation.GetColor`/`GetLabel` handle `NullMx`.

- [ ] **Step 1: Update the existing null-MX test's expected status**

In `test/DotMarc.Tests/Dns/MxDnsCheckerTests.cs`, change `CheckAsync_ReturnsOk_WhenNullMxIsPublished`:
```csharp
[Fact]
public async Task CheckAsync_ReturnsNullMx_WhenNullMxIsPublished()
{
    var (checker, handler) = CreateChecker();
    handler.ResponseBody = """
        {"Status":0,"Answer":[{"type":15,"data":"0 ."}]}
        """;

    var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

    Assert.Equal(MxCheckStatus.NullMx, result.Status);
    Assert.NotNull(result.Detail);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~MxDnsCheckerTests.CheckAsync_ReturnsNullMx_WhenNullMxIsPublished`
Expected: FAIL - `Assert.Equal() Failure: Expected: NullMx, Actual: Ok`

- [ ] **Step 3: Change the checker's null-MX branch**

In `src/DotMarc/Dns/MxDnsChecker.cs`, inside `CheckAsync`, change:
```csharp
if (mxAnswers.Count == 1 && mxAnswers[0].Exchange == ".")
{
    return new MxCheckResult(MxCheckStatus.Ok, "Explicit null MX (RFC 7505) - this domain intentionally does not accept mail.");
}
```
to:
```csharp
if (mxAnswers.Count == 1 && mxAnswers[0].Exchange == ".")
{
    return new MxCheckResult(MxCheckStatus.NullMx, "Explicit null MX (RFC 7505) - this domain intentionally does not accept mail.");
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test --filter FullyQualifiedName~MxDnsCheckerTests`
Expected: PASS (all `MxDnsCheckerTests` tests green)

- [ ] **Step 5: Update `MxStatusPresentation` for the new status**

Replace the full contents of `src/DotMarc/Reporting/MxStatusPresentation.cs`:
```csharp
using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps MxCheckStatus to the MudBlazor color/label pair used on DomainDetail.razor's
/// Overview health checklist - same shared-presentation-logic precedent as
/// DmarcStatusPresentation.</summary>
public static class MxStatusPresentation
{
    public static Color GetColor(MxCheckStatus status) => status switch
    {
        MxCheckStatus.Ok or MxCheckStatus.NullMx => Color.Success,
        MxCheckStatus.UnresolvableTarget => Color.Warning,
        MxCheckStatus.MissingRecord => Color.Error,
        _ => Color.Default
    };

    public static string GetLabel(MxCheckStatus status) => status switch
    {
        MxCheckStatus.Ok => "OK",
        MxCheckStatus.NullMx => "Null MX (no inbound mail)",
        MxCheckStatus.MissingRecord => "No MX record",
        MxCheckStatus.UnresolvableTarget => "Target does not resolve",
        _ => "Not checked yet"
    };
}
```

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test`
Expected: PASS, no failures

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Dns/MxDnsChecker.cs src/DotMarc/Reporting/MxStatusPresentation.cs test/DotMarc.Tests/Dns/MxDnsCheckerTests.cs
git commit -m "Distinguish null MX from a generic passing MX check"
```

---

### Task 3: `SpfDnsChecker` - detect the `v=spf1 -all` null pattern

**Files:**
- Modify: `src/DotMarc/Dns/SpfDnsChecker.cs`
- Modify: `src/DotMarc/Reporting/SpfStatusPresentation.cs`
- Test: `test/DotMarc.Tests/Dns/SpfDnsCheckerTests.cs`

**Interfaces:**
- Consumes: `SpfCheckStatus.NullSpf` (Task 1).
- Produces: `SpfDnsChecker.CheckAsync` returns `SpfCheckResult(SpfCheckStatus.NullSpf, "<domain> publishes a null SPF record (v=spf1 -all) - no senders are authorized to send mail as this domain.")` when the record is exactly `v=spf1 -all` (whitespace-tolerant, case-insensitive on `-all`). `SpfStatusPresentation.GetColor`/`GetLabel` handle `NullSpf`.

- [ ] **Step 1: Write the two new failing tests**

Add to `test/DotMarc.Tests/Dns/SpfDnsCheckerTests.cs` (inside the `SpfDnsCheckerTests` class, after `CheckAsync_ReturnsOk_WhenExactlyOneSpfRecordExists`):
```csharp
[Fact]
public async Task CheckAsync_ReturnsNullSpf_WhenTheRecordIsExactlyVEqualsSpf1DashAll()
{
    var (checker, handler) = CreateChecker();
    handler.ResponseBody = """
        {"Status":0,"Answer":[{"type":16,"data":"\"v=spf1 -all\""}]}
        """;

    var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

    Assert.Equal(SpfCheckStatus.NullSpf, result.Status);
    Assert.Contains("no senders are authorized", result.Detail);
}

[Fact]
public async Task CheckAsync_ReturnsOk_NotNullSpf_WhenDashAllFollowsARealMechanism()
{
    var (checker, handler) = CreateChecker();
    handler.ResponseBody = """
        {"Status":0,"Answer":[{"type":16,"data":"\"v=spf1 include:_spf.google.com -all\""}]}
        """;

    var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

    Assert.Equal(SpfCheckStatus.Ok, result.Status);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SpfDnsCheckerTests.CheckAsync_ReturnsNullSpf_WhenTheRecordIsExactlyVEqualsSpf1DashAll`
Expected: FAIL - `Assert.Equal() Failure: Expected: NullSpf, Actual: Ok` (the second new test already passes since current behavior is already `Ok` - that's fine, it's a regression guard for the next step, not a red/green step)

- [ ] **Step 3: Add the null-pattern check to `SpfDnsChecker.CheckAsync`**

In `src/DotMarc/Dns/SpfDnsChecker.cs`, change:
```csharp
        if (spfRecords.Count > 1)
        {
            return new SpfCheckResult(SpfCheckStatus.MultipleRecords, $"{domainName} has {spfRecords.Count} SPF records - RFC 7208 requires exactly one");
        }
        return new SpfCheckResult(SpfCheckStatus.Ok, null);
    }
```
to:
```csharp
        if (spfRecords.Count > 1)
        {
            return new SpfCheckResult(SpfCheckStatus.MultipleRecords, $"{domainName} has {spfRecords.Count} SPF records - RFC 7208 requires exactly one");
        }

        // The spfRecords[0]["v=spf1".Length..] slice is safe because spfRecords was already
        // filtered to records StartsWith("v=spf1", ...) above.
        var mechanisms = spfRecords[0]["v=spf1".Length..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (mechanisms.Length == 1 && string.Equals(mechanisms[0], "-all", StringComparison.OrdinalIgnoreCase))
        {
            return new SpfCheckResult(SpfCheckStatus.NullSpf, $"{domainName} publishes a null SPF record (v=spf1 -all) - no senders are authorized to send mail as this domain.");
        }

        return new SpfCheckResult(SpfCheckStatus.Ok, null);
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~SpfDnsCheckerTests`
Expected: PASS (all `SpfDnsCheckerTests` tests green, including both new ones)

- [ ] **Step 5: Update `SpfStatusPresentation` for the new status**

Replace the full contents of `src/DotMarc/Reporting/SpfStatusPresentation.cs`:
```csharp
using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps SpfCheckStatus to the MudBlazor color/label pair used on DomainDetail.razor's
/// Overview health checklist - same shared-presentation-logic precedent as
/// DmarcStatusPresentation.</summary>
public static class SpfStatusPresentation
{
    public static Color GetColor(SpfCheckStatus status) => status switch
    {
        SpfCheckStatus.Ok or SpfCheckStatus.NullSpf => Color.Success,
        SpfCheckStatus.MultipleRecords => Color.Warning,
        SpfCheckStatus.MissingRecord or SpfCheckStatus.Misconfigured => Color.Error,
        _ => Color.Default
    };

    public static string GetLabel(SpfCheckStatus status) => status switch
    {
        SpfCheckStatus.Ok => "OK",
        SpfCheckStatus.NullSpf => "Null SPF (no senders)",
        SpfCheckStatus.MissingRecord => "No SPF record",
        SpfCheckStatus.MultipleRecords => "Multiple SPF records",
        SpfCheckStatus.Misconfigured => "Misconfigured",
        _ => "Not checked yet"
    };
}
```

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test`
Expected: PASS, no failures

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Dns/SpfDnsChecker.cs src/DotMarc/Reporting/SpfStatusPresentation.cs test/DotMarc.Tests/Dns/SpfDnsCheckerTests.cs
git commit -m "Detect the SPF null-routed pattern (v=spf1 -all)"
```

---

### Task 4: `AlertingService` - suppress missing-report and flag unexpected activity for null-routed domains

**Files:**
- Modify: `src/DotMarc/Notifications/AlertingService.cs`
- Test: `test/DotMarc.Tests/Notifications/AlertingServiceTests.cs`

**Interfaces:**
- Consumes: `SpfCheckStatus.NullSpf` (Task 1), `Domain.SpfCheckStatus` (existing field).
- Produces: new `IAlertingService` method `Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, CancellationToken cancellationToken = default)`. `CheckPinnedDomainsAsync` skips the missing-report check (and resolves any stale `MissedReport` alert) for any domain whose `SpfCheckStatus == SpfCheckStatus.NullSpf`. Both are consumed by Task 5 (`PollingService`).

- [ ] **Step 1: Write the three new failing tests**

Add to `test/DotMarc.Tests/Notifications/AlertingServiceTests.cs`. First, a small seeding helper alongside the existing `SeedMonitoredDomainAsync` (add right after it):
```csharp
    private async Task SeedNullRoutedDomainAsync(string name)
    {
        await using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = name,
            IsMonitored = true,
            FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-10),
            LastReportReceivedUtc = null,
            SpfCheckStatus = SpfCheckStatus.NullSpf
        });
        await context.SaveChangesAsync();
    }
```
Then the three tests (add after `CheckPinnedDomainsAsync_ExplainsWhenAMonitoredDomainHasNeverReceivedAReport`):
```csharp
    [Fact]
    public async Task CheckPinnedDomainsAsync_SkipsMissingReportCheck_ForANullRoutedDomain()
    {
        await SeedSettingsAsync();
        await SeedNullRoutedDomainAsync("contoso.io");

        var fakeNotifier = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);

        await service.CheckPinnedDomainsAsync();

        await using var verifyContext = CreateContext();
        Assert.Empty(verifyContext.AlertEvents);
    }

    [Fact]
    public async Task CheckPinnedDomainsAsync_ResolvesStaleMissedReportAlert_ForADomainThatBecameNullRouted()
    {
        await SeedSettingsAsync();
        await using (var context = CreateContext())
        {
            context.Domains.Add(new Domain
            {
                Name = "contoso.io",
                IsMonitored = true,
                FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-10),
                LastReportReceivedUtc = null,
                SpfCheckStatus = SpfCheckStatus.NullSpf
            });
            context.AlertEvents.Add(new AlertEvent
            {
                DomainName = "contoso.io",
                AlertType = "MissedReport",
                Severity = "Warning",
                Title = "Missing expected DMARC report",
                Message = "stale, from before this domain became null-routed",
                CreatedUtc = DateTimeOffset.UtcNow.AddDays(-5)
            });
            await context.SaveChangesAsync();
        }

        var service = new AlertingService(new FakeDbContextFactory(_connectionString), new FakeAlertWebhookClient(), CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);

        await service.CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        var alert = await verify.AlertEvents.SingleAsync();
        Assert.True(alert.IsResolved);
        Assert.NotNull(alert.ResolvedUtc);
    }

    [Fact]
    public async Task FlagUnexpectedActivityForNullRoutedDomainAsync_CreatesAnAlert()
    {
        await SeedSettingsAsync();
        var fakeNotifier = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);

        await service.FlagUnexpectedActivityForNullRoutedDomainAsync("contoso.io", CancellationToken.None);

        await using var verify = CreateContext();
        var alert = await verify.AlertEvents.SingleAsync();
        Assert.Equal("UnexpectedActivityOnNullRoutedDomain", alert.AlertType);
        Assert.Equal("contoso.io", alert.DomainName);
        Assert.Contains("null-routed", alert.Message);
        Assert.Equal(1, fakeNotifier.CallCount);
    }
```
Also add `using DotMarc.Data;` to the top of the file if not already present (it already is, per the existing `SeedMonitoredDomainAsync` using `Domain`).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~AlertingServiceTests`
Expected: `CheckPinnedDomainsAsync_SkipsMissingReportCheck_ForANullRoutedDomain` and `CheckPinnedDomainsAsync_ResolvesStaleMissedReportAlert_ForADomainThatBecameNullRouted` FAIL (a `MissedReport` alert is created since nothing suppresses it yet); `FlagUnexpectedActivityForNullRoutedDomainAsync_CreatesAnAlert` FAILS TO COMPILE (`IAlertingService` has no such method yet) - expected, fixed by the next step.

- [ ] **Step 3: Add the interface method and null-routed branch**

In `src/DotMarc/Notifications/AlertingService.cs`, add to the `IAlertingService` interface (after `HandleTlsrptReportAsync`):
```csharp
    Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, CancellationToken cancellationToken = default);
```

Change `CheckPinnedDomainsAsync`'s loop body from:
```csharp
        foreach (var domain in domains)
        {
            if (domain.LastReportReceivedUtc is { } lastReport && lastReport >= cutoffUtc)
            {
                await ResolveDomainAlertAsync(domain.Name, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var message = domain.LastReportReceivedUtc is { } receivedUtc
                ? $"The monitored domain '{domain.Name}' has not received a DMARC report since {receivedUtc:O}."
                : $"The monitored domain '{domain.Name}' has not received a DMARC report yet.";
            await EnsureAlertAsync(db, settings, domain.Name, "MissedReport", "Warning", "Missing expected DMARC report", message, cancellationToken).ConfigureAwait(false);
        }
```
to:
```csharp
        foreach (var domain in domains)
        {
            if (domain.SpfCheckStatus == SpfCheckStatus.NullSpf)
            {
                // Null-routed (SPF v=spf1 -all): no reports is the expected, healthy state, not a
                // problem - resolve any pre-existing alert from before the domain became
                // null-routed and skip the missing-report check entirely for it.
                await ResolveDomainAlertAsync(domain.Name, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (domain.LastReportReceivedUtc is { } lastReport && lastReport >= cutoffUtc)
            {
                await ResolveDomainAlertAsync(domain.Name, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var message = domain.LastReportReceivedUtc is { } receivedUtc
                ? $"The monitored domain '{domain.Name}' has not received a DMARC report since {receivedUtc:O}."
                : $"The monitored domain '{domain.Name}' has not received a DMARC report yet.";
            await EnsureAlertAsync(db, settings, domain.Name, "MissedReport", "Warning", "Missing expected DMARC report", message, cancellationToken).ConfigureAwait(false);
        }
```

Add the new method implementation (after `HandleTlsrptReportAsync`, before `private async Task ResolveAlertAsync`):
```csharp
    public async Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await NotificationSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        var message = $"'{domainName}' is marked null-routed (SPF v=spf1 -all - no authorized senders) but a DMARC aggregate report just arrived showing mail activity. This may be legitimate traffic that needs accounting for, or a spoofing attempt.";
        await EnsureAlertAsync(db, settings, domainName, "UnexpectedActivityOnNullRoutedDomain", "Warning", "Unexpected mail activity on a null-routed domain", message, cancellationToken).ConfigureAwait(false);
    }
```

Add `using DotMarc.Data;` is already present at the top of the file (needed for `SpfCheckStatus`) - confirm it's there; it is (`Domain` is already used).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AlertingServiceTests`
Expected: PASS (all `AlertingServiceTests` tests green)

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS, no failures

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Notifications/AlertingService.cs test/DotMarc.Tests/Notifications/AlertingServiceTests.cs
git commit -m "Invert alerting for null-routed domains"
```

---

### Task 5: Wire the null-routed alert into report processing

**Files:**
- Modify: `src/DotMarc/Ingestion/PollingService.cs`
- Create: `test/DotMarc.Tests/Internal/FakeAlertingService.cs`
- Test: `test/DotMarc.Tests/Ingestion/PollingServiceTests.cs`

**Interfaces:**
- Consumes: `IAlertingService.FlagUnexpectedActivityForNullRoutedDomainAsync` (Task 4), `Domain.SpfCheckStatus` (existing).
- Produces: `ProcessMessageAsync` calls `FlagUnexpectedActivityForNullRoutedDomainAsync` for any domain with `SpfCheckStatus == SpfCheckStatus.NullSpf` when a report is successfully stored for it.

- [ ] **Step 1: Create the `FakeAlertingService` test double**

Create `test/DotMarc.Tests/Internal/FakeAlertingService.cs`:
```csharp
using DotMarc.Notifications;

namespace DotMarc.Tests.Internal;

internal sealed class FakeAlertingService : IAlertingService
{
    public List<string> ResolvedDomains { get; } = [];
    public List<string> FlaggedNullRoutedDomains { get; } = [];

    public Task CheckPinnedDomainsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ResolveDomainAlertAsync(string domainName, CancellationToken cancellationToken = default)
    {
        ResolvedDomains.Add(domainName);
        return Task.CompletedTask;
    }

    public Task HandleTlsrptReportAsync(string domainName, long failedSessionCount, IReadOnlyList<string> failureTypes, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, CancellationToken cancellationToken = default)
    {
        FlaggedNullRoutedDomains.Add(domainName);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Write the two new failing tests**

Add to `test/DotMarc.Tests/Ingestion/PollingServiceTests.cs` (after `PollOnceAsync_ParsesAndStoresAValidReport_ThenMarksMessageRead`):
```csharp
    [Fact]
    public async Task PollOnceAsync_FlagsUnexpectedActivity_WhenAReportArrivesForANullRoutedDomain()
    {
        using (var seed = CreateContext())
        {
            seed.Domains.Add(new Domain
            {
                Name = "contoso.io",
                FirstSeenUtc = DateTimeOffset.UtcNow,
                SpfCheckStatus = SpfCheckStatus.NullSpf
            });
            await seed.SaveChangesAsync();
        }

        var graphClient = new FakeGraphMailboxClient();
        graphClient.UnreadMessages.Add(new MailboxMessage("msg-1", "Report domain: contoso.io", true));
        graphClient.Attachments["msg-1"] = [new MailboxAttachment("report.xml.gz", "application/gzip", GzipOf(ValidReportXml))];

        var alertingService = new FakeAlertingService();
        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, alertingService, NullLogger<PollingService>.Instance);
            await service.PollOnceAsync(CancellationToken.None);
        }

        Assert.Contains("contoso.io", alertingService.FlaggedNullRoutedDomains);
    }

    [Fact]
    public async Task PollOnceAsync_DoesNotFlagUnexpectedActivity_ForANormalDomain()
    {
        var graphClient = new FakeGraphMailboxClient();
        graphClient.UnreadMessages.Add(new MailboxMessage("msg-1", "Report domain: contoso.io", true));
        graphClient.Attachments["msg-1"] = [new MailboxAttachment("report.xml.gz", "application/gzip", GzipOf(ValidReportXml))];

        var alertingService = new FakeAlertingService();
        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, alertingService, NullLogger<PollingService>.Instance);
            await service.PollOnceAsync(CancellationToken.None);
        }

        Assert.Empty(alertingService.FlaggedNullRoutedDomains);
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~PollingServiceTests.PollOnceAsync_FlagsUnexpectedActivity_WhenAReportArrivesForANullRoutedDomain`
Expected: FAIL - `Assert.Contains() Failure` (nothing calls the new method yet)

- [ ] **Step 4: Wire the call into `ProcessMessageAsync`**

In `src/DotMarc/Ingestion/PollingService.cs`, change:
```csharp
                if (_alertingService is not null)
                {
                    await _alertingService.ResolveDomainAlertAsync(domain.Name, cancellationToken).ConfigureAwait(false);
                }
```
(inside `ProcessMessageAsync`, right after `RecordProcessedMessageAsync`) to:
```csharp
                if (_alertingService is not null)
                {
                    await _alertingService.ResolveDomainAlertAsync(domain.Name, cancellationToken).ConfigureAwait(false);

                    if (domain.SpfCheckStatus == SpfCheckStatus.NullSpf)
                    {
                        await _alertingService.FlagUnexpectedActivityForNullRoutedDomainAsync(domain.Name, cancellationToken).ConfigureAwait(false);
                    }
                }
```
`domain` here is the entity `StoreReportAsync` (called immediately above in the same method) loaded fresh from `context.Domains`, so `SpfCheckStatus` reflects its latest persisted value from the independent SPF check cycle - no extra query needed. `EnsureAlertAsync`'s existing cooldown logic already prevents duplicate-alert spam if this fires more than once in quick succession.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~PollingServiceTests`
Expected: PASS (all `PollingServiceTests` tests green, including both new ones)

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test`
Expected: PASS, no failures

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Ingestion/PollingService.cs test/DotMarc.Tests/Internal/FakeAlertingService.cs test/DotMarc.Tests/Ingestion/PollingServiceTests.cs
git commit -m "Flag unexpected mail activity on null-routed domains"
```

---

### Task 6: Dashboard - skip "Missing" and show a "Null-routed" chip

**Files:**
- Modify: `src/DotMarc/Reporting/DashboardSummary.cs`
- Modify: `src/DotMarc/Components/Pages/Dashboard.razor`
- Test: `test/DotMarc.Tests/Reporting/DashboardSummaryTests.cs`

**Interfaces:**
- Consumes: `SpfCheckStatus.NullSpf` (Task 1), `Domain.SpfCheckStatus` (existing).
- Produces: `DashboardDomainRow` gains a new field `SpfCheckStatus` (appended after the existing `MtaStsStatus` field - the full new record shape is `DashboardDomainRow(int Id, string Name, string Status, Color StatusColor, double? PassRate, DateTimeOffset? LastReportReceivedUtc, bool IsMonitored, DmarcCheckStatus DmarcCheckStatus, DmarcAuthorizationCheckStatus DmarcAuthorizationCheckStatus, MtaStsStatus MtaStsStatus, SpfCheckStatus SpfCheckStatus)`). `Dashboard.razor` reads `context.SpfCheckStatus` to show the chip - this is what Task 8/9 do NOT touch (Dashboard has no mail-service badges per the spec's explicit UI-placement decision).

- [ ] **Step 1: Write the two new failing tests**

Add to `test/DotMarc.Tests/Reporting/DashboardSummaryTests.cs` (after `Build_MarksMonitoredDomainMissing_WhenLastReportOlderThanTwoDays`). First, update the `DomainWith` helper to accept an optional `SpfCheckStatus` (default `NotChecked`, matching every other test's implicit default):
```csharp
    private static Domain DomainWith(string name, bool isMonitored, DateTimeOffset? lastReportReceivedUtc, params Report[] reports) =>
        DomainWith(name, isMonitored, lastReportReceivedUtc, SpfCheckStatus.NotChecked, reports);

    private static Domain DomainWith(string name, bool isMonitored, DateTimeOffset? lastReportReceivedUtc, SpfCheckStatus spfCheckStatus, params Report[] reports)
    {
        var domain = new Domain { Name = name, FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = isMonitored, LastReportReceivedUtc = lastReportReceivedUtc, SpfCheckStatus = spfCheckStatus };
        domain.Reports.AddRange(reports);
        return domain;
    }
```
(This replaces the single existing `DomainWith` method - every existing call site keeps compiling unchanged since the 4-arg overload is preserved with the same signature.)

Then the two new tests:
```csharp
    [Fact]
    public void Build_NeverMarksANullRoutedDomainMissing_RegardlessOfLastReportReceivedUtc()
    {
        var domain = DomainWith("contoso.io", isMonitored: true, lastReportReceivedUtc: null, SpfCheckStatus.NullSpf);

        var (summary, rows) = DashboardSummary.Build([domain], parseFailureCount: 0);

        Assert.NotEqual("Missing", Assert.Single(rows).Status);
        Assert.Equal(0, summary.MissingCount);
    }

    [Fact]
    public void Build_IncludesSpfCheckStatus_OnEachRow()
    {
        var domain = DomainWith("contoso.io", isMonitored: true, DateTimeOffset.UtcNow, SpfCheckStatus.NullSpf);

        var (_, rows) = DashboardSummary.Build([domain], parseFailureCount: 0);

        Assert.Equal(SpfCheckStatus.NullSpf, Assert.Single(rows).SpfCheckStatus);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~DashboardSummaryTests`
Expected: FAILS TO COMPILE - `DashboardDomainRow` has no `SpfCheckStatus` member yet, and the new `DomainWith` overload doesn't exist yet either (expected; fixed by the next step, which adds the overload and the field together).

- [ ] **Step 3: Add `SpfCheckStatus` to `DashboardDomainRow` and skip null-routed domains in the missing-report computation**

In `src/DotMarc/Reporting/DashboardSummary.cs`, change:
```csharp
                var passRate = DomainStatistics.GetPassRate(d.Reports);

                var missingReport = d.IsMonitored && (d.LastReportReceivedUtc is null || d.LastReportReceivedUtc < DateTimeOffset.UtcNow.AddDays(-2));
                var status = missingReport ? "Missing" : passRate is null or >= 0.95 ? "OK" : "Warning";
                var color = status switch { "Missing" => Color.Error, "Warning" => Color.Warning, _ => Color.Success };

                return new DashboardDomainRow(d.Id, d.Name, status, color, passRate, d.LastReportReceivedUtc, d.IsMonitored, d.DmarcCheckStatus, d.DmarcAuthorizationCheckStatus, d.MtaStsStatus);
```
to:
```csharp
                var passRate = DomainStatistics.GetPassRate(d.Reports);

                var isNullRouted = d.SpfCheckStatus == SpfCheckStatus.NullSpf;
                var missingReport = d.IsMonitored && !isNullRouted && (d.LastReportReceivedUtc is null || d.LastReportReceivedUtc < DateTimeOffset.UtcNow.AddDays(-2));
                var status = missingReport ? "Missing" : passRate is null or >= 0.95 ? "OK" : "Warning";
                var color = status switch { "Missing" => Color.Error, "Warning" => Color.Warning, _ => Color.Success };

                return new DashboardDomainRow(d.Id, d.Name, status, color, passRate, d.LastReportReceivedUtc, d.IsMonitored, d.DmarcCheckStatus, d.DmarcAuthorizationCheckStatus, d.MtaStsStatus, d.SpfCheckStatus);
```

Change the `DashboardDomainRow` record declaration at the bottom of the file from:
```csharp
public sealed record DashboardDomainRow(int Id, string Name, string Status, Color StatusColor, double? PassRate, DateTimeOffset? LastReportReceivedUtc, bool IsMonitored, DmarcCheckStatus DmarcCheckStatus, DmarcAuthorizationCheckStatus DmarcAuthorizationCheckStatus, MtaStsStatus MtaStsStatus);
```
to:
```csharp
public sealed record DashboardDomainRow(int Id, string Name, string Status, Color StatusColor, double? PassRate, DateTimeOffset? LastReportReceivedUtc, bool IsMonitored, DmarcCheckStatus DmarcCheckStatus, DmarcAuthorizationCheckStatus DmarcAuthorizationCheckStatus, MtaStsStatus MtaStsStatus, SpfCheckStatus SpfCheckStatus);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~DashboardSummaryTests`
Expected: PASS (all `DashboardSummaryTests` tests green)

- [ ] **Step 5: Add the "Null-routed" chip to `Dashboard.razor`**

In `src/DotMarc/Components/Pages/Dashboard.razor`, change the domain-name cell in `RowTemplate`:
```razor
            <MudTd @onclick="@(() => Navigation.NavigateTo($"/domains/{Uri.EscapeDataString(context.Name)}"))" Style="cursor:pointer">@context.Name</MudTd>
```
to:
```razor
            <MudTd @onclick="@(() => Navigation.NavigateTo($"/domains/{Uri.EscapeDataString(context.Name)}"))" Style="cursor:pointer">
                @context.Name
                @if (context.SpfCheckStatus == SpfCheckStatus.NullSpf)
                {
                    <MudChip T="string" Color="Color.Info" Size="Size.Small" Class="ml-2">Null-routed</MudChip>
                }
            </MudTd>
```
`@using DotMarc.Data` is already present at the top of `Dashboard.razor` (needed for `SpfCheckStatus`) - confirm it's there; it is (`DashboardDomainRow`'s other status fields, e.g. `DmarcCheckStatus`, are already referenced the same way).

- [ ] **Step 6: Build and manually verify**

Run: `dotnet build`
Expected: builds cleanly. (No automated UI test exists for `Dashboard.razor` in this codebase - manual verification is via the demo dataset once Task 7's DI wiring and a null-routed demo domain exist; skip if not convenient at this point in the plan.)

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test`
Expected: PASS, no failures

- [ ] **Step 8: Commit**

```bash
git add src/DotMarc/Reporting/DashboardSummary.cs src/DotMarc/Components/Pages/Dashboard.razor test/DotMarc.Tests/Reporting/DashboardSummaryTests.cs
git commit -m "Show null-routed domains on the Dashboard and skip their missing-report status"
```

---

### Task 7: `MailServiceDetector` - detect known mail services from MX and SPF

**Files:**
- Create: `src/DotMarc/Dns/IMailServiceDetector.cs`
- Create: `src/DotMarc/Dns/DetectedMailService.cs`
- Create: `src/DotMarc/Dns/MailServiceDetector.cs`
- Modify: `src/DotMarc/Program.cs`
- Test: `test/DotMarc.Tests/Dns/MailServiceDetectorTests.cs`

**Interfaces:**
- Produces: `DetectedMailServiceKind { Inbox, Sending }`, `DetectedMailService(string ProviderName, DetectedMailServiceKind Kind)`, `IMailServiceDetector.DetectAsync(string domainName, CancellationToken cancellationToken) -> Task<List<DetectedMailService>>` - consumed by Task 8 (`DomainDetail.razor` badges) and Task 9 (DKIM selector suggestions use the same provider name strings: `"Microsoft 365"`, `"Google Workspace"`, `"Zoho Mail"`, `"Fastmail"`, `"ProtonMail"` for inbox providers).

- [ ] **Step 1: Create the small types**

Create `src/DotMarc/Dns/DetectedMailService.cs`:
```csharp
namespace DotMarc.Dns;

public enum DetectedMailServiceKind
{
    Inbox,
    Sending
}

/// <summary>One mail service detected for a domain - ProviderName is a fixed display string (e.g.
/// "Microsoft 365"), Kind distinguishes an inbox provider (matched via MX) from a sending service
/// (matched via an SPF include: mechanism). A domain can produce zero, one, or several of these -
/// see MailServiceDetector's lookup tables.</summary>
public sealed record DetectedMailService(string ProviderName, DetectedMailServiceKind Kind);
```

Create `src/DotMarc/Dns/IMailServiceDetector.cs`:
```csharp
namespace DotMarc.Dns;

public interface IMailServiceDetector
{
    Task<List<DetectedMailService>> DetectAsync(string domainName, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Write the failing tests**

Create `test/DotMarc.Tests/Dns/MailServiceDetectorTests.cs`:
```csharp
using DotMarc.Dns;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class MailServiceDetectorTests
{
    private static (MailServiceDetector detector, FakeHttpMessageHandler handler) CreateDetector()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new MailServiceDetector(http), handler);
    }

    [Theory]
    [InlineData("contoso-com.mail.protection.outlook.com", "Microsoft 365")]
    [InlineData("aspmx.l.google.com", "Google Workspace")]
    [InlineData("alt1.aspmx.l.google.com", "Google Workspace")]
    [InlineData("mx.zoho.com", "Zoho Mail")]
    [InlineData("mx2.zohomail.com", "Zoho Mail")]
    [InlineData("mx.zohomail.eu", "Zoho Mail")]
    [InlineData("mx.zohomail.in", "Zoho Mail")]
    [InlineData("in1-smtp.messagingengine.com", "Fastmail")]
    [InlineData("mail.protonmail.ch", "ProtonMail")]
    public async Task DetectAsync_DetectsInboxProvider_FromMxExchange(string mxExchange, string expectedProvider)
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBodies.Enqueue($$"""{"Status":0,"Answer":[{"type":15,"data":"10 {{mxExchange}}."}]}""");
        handler.ResponseBodies.Enqueue("""{"Status":3}""");

        var result = await detector.DetectAsync("contoso.io", CancellationToken.None);

        var service = Assert.Single(result);
        Assert.Equal(expectedProvider, service.ProviderName);
        Assert.Equal(DetectedMailServiceKind.Inbox, service.Kind);
    }

    [Theory]
    [InlineData("_spf.google.com", "Google Workspace")]
    [InlineData("spf.protection.outlook.com", "Microsoft 365")]
    [InlineData("zoho.com", "Zoho")]
    [InlineData("zoho.eu", "Zoho")]
    [InlineData("servers.mcsv.net", "Mailchimp")]
    [InlineData("sendgrid.net", "SendGrid")]
    [InlineData("amazonses.com", "Amazon SES")]
    [InlineData("_spf.salesforce.com", "Salesforce")]
    public async Task DetectAsync_DetectsSendingService_FromSpfInclude(string includeHost, string expectedProvider)
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBodies.Enqueue("""{"Status":3}""");
        handler.ResponseBodies.Enqueue($$"""{"Status":0,"Answer":[{"type":16,"data":"\"v=spf1 include:{{includeHost}} ~all\""}]}""");

        var result = await detector.DetectAsync("contoso.io", CancellationToken.None);

        var service = Assert.Single(result);
        Assert.Equal(expectedProvider, service.ProviderName);
        Assert.Equal(DetectedMailServiceKind.Sending, service.Kind);
    }

    [Fact]
    public async Task DetectAsync_ReturnsBothInboxAndSendingMatches_WhenBothArePresent()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBodies.Enqueue("""{"Status":0,"Answer":[{"type":15,"data":"1 aspmx.l.google.com."}]}""");
        handler.ResponseBodies.Enqueue("""{"Status":0,"Answer":[{"type":16,"data":"\"v=spf1 include:servers.mcsv.net ~all\""}]}""");

        var result = await detector.DetectAsync("contoso.io", CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, s => s.ProviderName == "Google Workspace" && s.Kind == DetectedMailServiceKind.Inbox);
        Assert.Contains(result, s => s.ProviderName == "Mailchimp" && s.Kind == DetectedMailServiceKind.Sending);
    }

    [Fact]
    public async Task DetectAsync_ReturnsEmptyList_WhenNothingMatches()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBodies.Enqueue("""{"Status":0,"Answer":[{"type":15,"data":"10 mail.contoso.io."}]}""");
        handler.ResponseBodies.Enqueue("""{"Status":0,"Answer":[{"type":16,"data":"\"v=spf1 include:_spf.unknown-example.test ~all\""}]}""");

        var result = await detector.DetectAsync("contoso.io", CancellationToken.None);

        Assert.Empty(result);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail (compile error)**

Run: `dotnet test --filter FullyQualifiedName~MailServiceDetectorTests`
Expected: FAILS TO COMPILE - `MailServiceDetector` doesn't exist yet.

- [ ] **Step 4: Implement `MailServiceDetector`**

Create `src/DotMarc/Dns/MailServiceDetector.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.Dns;

/// <summary>Detects known mail services from a domain's MX (inbox provider) and SPF include:
/// mechanisms (sending service) - informational only, no push/remediation action. Does its own raw
/// MX and TXT queries rather than reusing MxDnsChecker/SpfDnsChecker - same "small, independent
/// checker, no shared abstraction" precedent as every other checker in this codebase (see
/// MxDnsChecker's own doc comment).</summary>
public sealed class MailServiceDetector : IMailServiceDetector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    private static readonly (string Suffix, string Provider)[] InboxProviders =
    [
        (".mail.protection.outlook.com", "Microsoft 365"),
        ("aspmx.l.google.com", "Google Workspace"),
        (".zoho.com", "Zoho Mail"),
        (".zohomail.com", "Zoho Mail"),
        (".zohomail.eu", "Zoho Mail"),
        (".zohomail.in", "Zoho Mail"),
        (".messagingengine.com", "Fastmail"),
        (".protonmail.ch", "ProtonMail")
    ];

    private static readonly (string Suffix, string Provider)[] SendingServices =
    [
        ("_spf.google.com", "Google Workspace"),
        ("spf.protection.outlook.com", "Microsoft 365"),
        ("zoho.com", "Zoho"),
        ("zoho.eu", "Zoho"),
        ("servers.mcsv.net", "Mailchimp"),
        ("sendgrid.net", "SendGrid"),
        ("amazonses.com", "Amazon SES"),
        ("_spf.salesforce.com", "Salesforce")
    ];

    public MailServiceDetector(HttpClient http) => _http = http;

    public async Task<List<DetectedMailService>> DetectAsync(string domainName, CancellationToken cancellationToken)
    {
        var results = new List<DetectedMailService>();

        var mxExchanges = await QueryMxExchangesAsync(domainName, cancellationToken).ConfigureAwait(false);
        foreach (var (suffix, provider) in InboxProviders)
        {
            if (results.Any(r => r.Kind == DetectedMailServiceKind.Inbox && r.ProviderName == provider))
            {
                continue;
            }
            if (mxExchanges.Any(exchange => exchange.TrimEnd('.').EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new DetectedMailService(provider, DetectedMailServiceKind.Inbox));
            }
        }

        var spfIncludeHosts = await QuerySpfIncludeHostsAsync(domainName, cancellationToken).ConfigureAwait(false);
        foreach (var (suffix, provider) in SendingServices)
        {
            if (results.Any(r => r.Kind == DetectedMailServiceKind.Sending && r.ProviderName == provider))
            {
                continue;
            }
            if (spfIncludeHosts.Any(host => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new DetectedMailService(provider, DetectedMailServiceKind.Sending));
            }
        }

        return results;
    }

    private async Task<List<string>> QueryMxExchangesAsync(string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type=MX");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;

        var exchanges = new List<string>();
        foreach (var answer in (parsed.Answer ?? []).Where(a => a.Type == 15))
        {
            var parts = answer.Data.Split(' ', 2);
            if (parts.Length == 2)
            {
                exchanges.Add(parts[1]);
            }
        }
        return exchanges;
    }

    private async Task<List<string>> QuerySpfIncludeHostsAsync(string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type=TXT");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;

        var txtRecords = (parsed.Answer ?? [])
            .Where(a => a.Type == 16)
            .Select(a => string.Join("", a.Data.Split("\" \"")).Trim('"'));

        var spfRecord = txtRecords.FirstOrDefault(r => r.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase));
        if (spfRecord is null)
        {
            return [];
        }

        return spfRecord
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.StartsWith("include:", StringComparison.OrdinalIgnoreCase))
            .Select(token => token["include:".Length..])
            .ToList();
    }

    private sealed record DnsOverHttpsResponse([property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~MailServiceDetectorTests`
Expected: PASS (all `MailServiceDetectorTests` tests green, including every `[Theory]` case)

- [ ] **Step 6: Register the DI client in `Program.cs`**

In `src/DotMarc/Program.cs`, add after the existing `IDkimDnsChecker` registration block:
```csharp
builder.Services.AddHttpClient<DotMarc.Dns.IMailServiceDetector, DotMarc.Dns.MailServiceDetector>(client =>
{
    client.BaseAddress = new Uri("https://cloudflare-dns.com/");
    client.DefaultRequestHeaders.Add("Accept", "application/dns-json");
});
```

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test`
Expected: PASS, no failures

- [ ] **Step 8: Build to confirm DI wiring compiles**

Run: `dotnet build`
Expected: builds cleanly

- [ ] **Step 9: Commit**

```bash
git add src/DotMarc/Dns/IMailServiceDetector.cs src/DotMarc/Dns/DetectedMailService.cs src/DotMarc/Dns/MailServiceDetector.cs src/DotMarc/Program.cs test/DotMarc.Tests/Dns/MailServiceDetectorTests.cs
git commit -m "Add mail service detection from MX and SPF"
```

---

### Task 8: `DomainDetail.razor` - show detected mail service badges

**Files:**
- Modify: `src/DotMarc/Components/Pages/DomainDetail.razor`

**Interfaces:**
- Consumes: `IMailServiceDetector.DetectAsync` (Task 7).
- Produces: `DomainDetail`'s private field `_detectedMailServices` (`List<DetectedMailService>`) - consumed by Task 9 to pick the DKIM selector suggestion.

- [ ] **Step 1: Inject `IMailServiceDetector`**

In `src/DotMarc/Components/Pages/DomainDetail.razor`, add after the existing `@inject IDkimDnsChecker DkimDnsChecker` line:
```razor
@inject IMailServiceDetector MailServiceDetector
```

- [ ] **Step 2: Add the backing field**

Near the other private fields (alongside `private Domain? _domain;` at line 326), add:
```csharp
    private List<DetectedMailService> _detectedMailServices = [];
```

- [ ] **Step 3: Fetch the detected services once, on the interactive circuit only**

In `OnInitializedAsync`, inside the existing `if (RendererInfo.IsInteractive)` block (the one that also kicks off IP enrichment), add after the `foreach (var source in _sources)` loop closes but still inside the `if` block:
```csharp
            try
            {
                _detectedMailServices = await MailServiceDetector.DetectAsync(DomainName, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Informational-only badges - a transient DNS failure here shouldn't disrupt the
                // rest of the page load. Logged so a persistent failure is still diagnosable.
                Logger.LogWarning(ex, "Failed to detect mail services for {DomainName}.", DomainName);
            }
```
This runs only once per real page view - the same reasoning that already gates IP enrichment to `RendererInfo.IsInteractive` (Blazor Server's static prerender pass would otherwise double every live network call) applies equally to this live DNS lookup.

- [ ] **Step 4: Render the badges next to the "Domain health" heading**

Change:
```razor
                <div class="d-flex align-center mb-2">
                    <MudText Typo="Typo.subtitle1">Domain health</MudText>
                    <DocsLink Href="https://dotmarc.app/docs/scope" Text="What dotMARC monitors for a domain" Class="ml-2" />
                </div>
```
to:
```razor
                <div class="d-flex align-center mb-2">
                    <MudText Typo="Typo.subtitle1">Domain health</MudText>
                    <DocsLink Href="https://dotmarc.app/docs/scope" Text="What dotMARC monitors for a domain" Class="ml-2" />
                </div>
                @if (_detectedMailServices.Count > 0)
                {
                    <div class="d-flex align-center flex-wrap mb-3" style="gap:6px;">
                        @foreach (var service in _detectedMailServices)
                        {
                            <MudChip T="string"
                                     Color="@(service.Kind == DetectedMailServiceKind.Inbox ? Color.Primary : Color.Secondary)"
                                     Size="Size.Small"
                                     Icon="@(service.Kind == DetectedMailServiceKind.Inbox ? Icons.Material.Filled.Inbox : Icons.Material.Filled.Send)">
                                @service.ProviderName
                            </MudChip>
                        }
                    </div>
                }
```

- [ ] **Step 5: Build**

Run: `dotnet build`
Expected: builds cleanly (`@using DotMarc.Dns` is already present at the top of `DomainDetail.razor` for `IMxDnsChecker` etc., so `DetectedMailService`/`DetectedMailServiceKind` resolve without a new `@using`)

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test`
Expected: PASS, no failures (no automated UI tests exist for this component in this codebase - this task has no new test file; manual verification happens in Task 9's step, once the demo dataset or a real domain can exercise it)

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Components/Pages/DomainDetail.razor
git commit -m "Show detected mail service badges on the domain detail Overview tab"
```

---

### Task 9: DKIM selector auto-suggestion

**Files:**
- Create: `src/DotMarc/Dns/DkimSelectorSuggestions.cs`
- Modify: `src/DotMarc/Components/Dialogs/ConfigureDkimSelectorsDialog.razor`
- Modify: `src/DotMarc/Components/Pages/DomainDetail.razor`
- Test: `test/DotMarc.Tests/Dns/DkimSelectorSuggestionsTests.cs`

**Interfaces:**
- Consumes: `_detectedMailServices` (Task 8), `DetectedMailServiceKind.Inbox` (Task 7).
- Produces: `DkimSelectorSuggestions.GetSuggestedSelectors(string providerName) -> List<string>?` (`null` for an unrecognized provider name). `ConfigureDkimSelectorsDialog` gains a new parameter `DetectedInboxProvider` (`string?`).

- [ ] **Step 1: Write the failing tests for the pure lookup**

Create `test/DotMarc.Tests/Dns/DkimSelectorSuggestionsTests.cs`:
```csharp
using DotMarc.Dns;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class DkimSelectorSuggestionsTests
{
    [Theory]
    [InlineData("Microsoft 365", new[] { "selector1", "selector2" })]
    [InlineData("Google Workspace", new[] { "google" })]
    [InlineData("Zoho Mail", new[] { "zoho1" })]
    [InlineData("Fastmail", new[] { "fm1", "fm2", "fm3" })]
    [InlineData("ProtonMail", new[] { "protonmail2", "protonmail3" })]
    public void GetSuggestedSelectors_ReturnsProviderSpecificSelectors(string provider, string[] expected)
    {
        var result = DkimSelectorSuggestions.GetSuggestedSelectors(provider);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetSuggestedSelectors_ReturnsNull_ForAnUnrecognizedProvider()
    {
        Assert.Null(DkimSelectorSuggestions.GetSuggestedSelectors("Some Unknown Provider"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail (compile error)**

Run: `dotnet test --filter FullyQualifiedName~DkimSelectorSuggestionsTests`
Expected: FAILS TO COMPILE - `DkimSelectorSuggestions` doesn't exist yet.

- [ ] **Step 3: Implement `DkimSelectorSuggestions`**

Create `src/DotMarc/Dns/DkimSelectorSuggestions.cs`:
```csharp
namespace DotMarc.Dns;

/// <summary>A pre-fill suggestion, not an applied value - ConfigureDkimSelectorsDialog uses this to
/// pre-populate its selectors text box for a domain with no selectors configured yet, editable or
/// clearable by the admin before saving. Scoped to the five inbox providers with a reliable,
/// universal MX-pattern-to-selector convention (matching MailServiceDetector's InboxProviders
/// table by provider name) - sending-only services have per-account DKIM setup that can't be
/// usefully pre-filled.</summary>
public static class DkimSelectorSuggestions
{
    private static readonly Dictionary<string, List<string>> SelectorsByProvider = new(StringComparer.Ordinal)
    {
        ["Microsoft 365"] = ["selector1", "selector2"],
        ["Google Workspace"] = ["google"],
        ["Zoho Mail"] = ["zoho1"],
        ["Fastmail"] = ["fm1", "fm2", "fm3"],
        ["ProtonMail"] = ["protonmail2", "protonmail3"]
    };

    public static List<string>? GetSuggestedSelectors(string providerName) =>
        SelectorsByProvider.TryGetValue(providerName, out var selectors) ? selectors : null;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~DkimSelectorSuggestionsTests`
Expected: PASS (all `DkimSelectorSuggestionsTests` tests green)

- [ ] **Step 5: Pre-fill the dialog**

Replace the full contents of `src/DotMarc/Components/Dialogs/ConfigureDkimSelectorsDialog.razor`:
```razor
@* src/DotMarc/Components/Dialogs/ConfigureDkimSelectorsDialog.razor *@
@using DotMarc.Dns
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
    [Parameter] public string? DetectedInboxProvider { get; set; }

    private string _selectorsText = "";

    protected override void OnInitialized()
    {
        if (CurrentSelectors.Count > 0)
        {
            _selectorsText = string.Join('\n', CurrentSelectors);
        }
        else if (DetectedInboxProvider is not null && DkimSelectorSuggestions.GetSuggestedSelectors(DetectedInboxProvider) is { } suggested)
        {
            _selectorsText = string.Join('\n', suggested);
        }
    }

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

- [ ] **Step 6: Pass the detected inbox provider from `DomainDetail.razor`**

In `src/DotMarc/Components/Pages/DomainDetail.razor`'s `OpenDkimSelectorsDialogAsync`, change:
```csharp
        var parameters = new DialogParameters<ConfigureDkimSelectorsDialog>
        {
            { x => x.DomainName, DomainName },
            { x => x.CurrentSelectors, _domain!.DkimSelectors }
        };
```
to:
```csharp
        var parameters = new DialogParameters<ConfigureDkimSelectorsDialog>
        {
            { x => x.DomainName, DomainName },
            { x => x.CurrentSelectors, _domain!.DkimSelectors },
            { x => x.DetectedInboxProvider, _detectedMailServices.FirstOrDefault(s => s.Kind == DetectedMailServiceKind.Inbox)?.ProviderName }
        };
```

- [ ] **Step 7: Build**

Run: `dotnet build`
Expected: builds cleanly

- [ ] **Step 8: Run the full test suite**

Run: `dotnet test`
Expected: PASS, no failures

- [ ] **Step 9: Manually verify in the browser**

Start the app locally (`dotnet exec bin/Debug/net10.0/DotMarc.dll` from `src/DotMarc/`, per this project's Windows Defender workaround for freshly-built `.exe`s - or `dotnet run` if that's not an issue in the current environment), open a domain whose MX resolves to a recognized inbox provider (e.g. a demo-mode domain, or any real monitored domain hosted on Microsoft 365/Google Workspace/Zoho/Fastmail/ProtonMail) with no DKIM selectors configured yet, and confirm:
- The Overview tab shows a mail service badge for the detected provider.
- Opening "Configure selectors" pre-fills the text box with that provider's suggested selector(s), still editable/clearable.
- A domain with selectors already configured opens the dialog showing its existing selectors unchanged (no suggestion overwrite).
- A domain with no MX match (or a recheck) opens the dialog empty, exactly as before this task.

Expected: all four behaviors match. Note in the final report whether this manual check could be completed (e.g., demo mode available) or not.

- [ ] **Step 10: Commit**

```bash
git add src/DotMarc/Dns/DkimSelectorSuggestions.cs src/DotMarc/Components/Dialogs/ConfigureDkimSelectorsDialog.razor src/DotMarc/Components/Pages/DomainDetail.razor test/DotMarc.Tests/Dns/DkimSelectorSuggestionsTests.cs
git commit -m "Pre-fill DKIM selector suggestions from the detected inbox provider"
```

---

## Final Verification

- [ ] Run the full test suite one more time: `dotnet test` - expect PASS, no failures.
- [ ] Run `dotnet build` - expect a clean build with no new warnings.
- [ ] Skim the spec's Non-goals section once more and confirm nothing in the implementation drifted past it: no automatic p=reject enforcement, no manual null-routed override anywhere in the UI, no push/remediation button on any mail-service badge, no DKIM selector suggestion for a sending-only service.
