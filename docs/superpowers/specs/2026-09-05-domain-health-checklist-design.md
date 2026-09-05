# Domain Health Checklist Design

## Problem

The domain detail page's Overview tab shows DMARC and TLS Reporting status as two separate panels, and MTA-STS status only lives on its own tab. This under-represents what dotMARC actually knows and can check:

- **DMARC** is checked as four things (own record exists, starts with `v=DMARC1`, `rua=` matches the configured mailbox, and — when that mailbox is on a different domain, the normal MSP shape — a cross-domain authorization record exists) but they all collapse into one `DmarcCheckStatus` field. Worse, the checker short-circuits: if the own record is broken, the authorization record is never even checked, so the two can never be shown as independent facts.
- **TLSRPT** collapses two sub-checks (own record exists, is well-formed) into one field, same shape as DMARC's own-record check.
- **MTA-STS** already has its own granular status but it's not summarized anywhere outside its dedicated tab.
- **SPF** and **MX** have no live DNS health check at all — only historical per-message pass/fail data parsed from other senders' aggregate reports (which reflects whether messages happened to pass, not whether the domain's own records are even present or sane).
- **DKIM** is in the same position as SPF/MX, and is fundamentally harder to check live: DKIM selectors (`<selector>._domainkey.<domain>`) are provider-specific and unknowable without being told.

## Goals

- Split DMARC's own-record check and its cross-domain authorization check into two independent, always-run checks, each with its own status field — so both can be shown (and pushed) independently regardless of the other's state.
- Add two new, always-on live DNS checks: SPF (record presence, exactly one record, basic syntax) and MX (presence or explicit RFC 7505 null-MX, target resolvability).
- Add DKIM as an opt-in check: an admin configures one or more selector names per domain; dotMARC then checks `<selector>._domainkey.<domain>` for each one.
- Replace the Overview tab's two separate status panels with one consolidated checklist showing all seven checks (DMARC record, DMARC authorization, TLSRPT, MTA-STS summary, SPF, MX, DKIM) with status, last-checked time, detail, and an action where one exists.

## Non-goals

- No auto-push for SPF or MX. Unlike DMARC/TLSRPT/MTA-STS/the DMARC authorization record — which all have exactly one deterministically correct value dotMARC can compute from its own configuration — the correct SPF record depends on which sending services a domain actually uses, and the correct MX depends on which mailbox provider it's on. dotMARC has no way to know either, so these are read-only health indicators only.
- No recursive SPF lookup-count validation (RFC 7208's 10-lookup limit, chasing `include:`/`redirect=` chains). Out of scope for this pass — flagged as a possible future enhancement.
- No changes to the Dashboard's grouped DNS Status column (shipped separately, just before this feature) — this spec is scoped to the domain detail page's Overview tab only.
- No mail-flow/connectivity testing (e.g. connecting to an MX host on port 25). MX's check is DNS-only: does it exist, and does the hostname resolve.

## Data model changes

New fields on `Domain` (`src/DotMarc/Data/Domain.cs`), one migration:

```csharp
// DMARC authorization record — split out of the existing DmarcCheckStatus field, which stops
// emitting MissingAuthorizationRecord going forward (existing rows self-correct on next check).
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

`DkimSelectors` follows the exact existing `MtaStsMxHosts` pattern in `DotMarcDbContext.OnModelCreating`: `HasConversion(hosts => hosts.ToArray(), stored => stored.ToList())` plus an explicit `ValueComparer<List<string>>` (required — EF Core throws at runtime without one, per the existing comment on `MtaStsMxHosts`). The four new enums get `HasConversion<string>()`, matching every existing status enum on `Domain`.

New enums (`src/DotMarc/Data/`, one file each, matching `DmarcCheckStatus.cs`'s existing shape):

```csharp
public enum DmarcAuthorizationCheckStatus
{
    NotChecked,
    NotApplicable, // mailbox domain == monitored domain: RFC 7489 §7.1 doesn't require this record
    Ok,
    Missing
}

public enum SpfCheckStatus
{
    NotChecked,
    Ok,
    MissingRecord,
    MultipleRecords, // more than one v=spf1 TXT record — invalid per RFC 7208, a common misconfig
    Misconfigured    // record found but doesn't start with v=spf1
}

public enum MxCheckStatus
{
    NotChecked,
    Ok,                 // resolving MX host(s), or an explicit RFC 7505 null MX ("0 .")
    MissingRecord,      // no MX records and no null-MX policy
    UnresolvableTarget  // has MX record(s) but at least one target has no A record
}

public enum DkimCheckStatus
{
    NotConfigured, // no selectors configured yet — neutral default, not a failure
    Ok,
    Missing,       // at least one configured selector has no TXT record
    Misconfigured  // record exists but isn't a plausible DKIM key (no p= tag)
}
```

## Checkers

### DMARC authorization split

`IDmarcDnsChecker` (`src/DotMarc/Dns/`) gains a second method on the same interface — this stays one cohesive "DMARC checker" class, not a new top-level checker, mirroring how `IMtaStsHostProvisioner` already exposes multiple related methods:

```csharp
public interface IDmarcDnsChecker
{
    Task<DmarcCheckResult> CheckAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken);
    Task<DmarcAuthorizationCheckResult> CheckAuthorizationAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken);
}

public sealed record DmarcAuthorizationCheckResult(DmarcAuthorizationCheckStatus Status, string? Detail);
```

`CheckAsync`'s existing body loses its trailing authorization-record branch (lines 42-52 of the current file) — it now returns `Ok` as soon as the own-record/prefix/rua checks pass, regardless of mailbox domain. `CheckAuthorizationAsync` is new, independent, and always runs (when the callers below decide to call it) regardless of what `CheckAsync` returned:

```csharp
public async Task<DmarcAuthorizationCheckResult> CheckAuthorizationAsync(string domainName, string mailboxAddress, CancellationToken cancellationToken)
{
    var mailboxDomain = mailboxAddress[(mailboxAddress.IndexOf('@') + 1)..];
    if (string.Equals(mailboxDomain, domainName, StringComparison.OrdinalIgnoreCase))
    {
        return new DmarcAuthorizationCheckResult(DmarcAuthorizationCheckStatus.NotApplicable, null);
    }

    var authorizationName = $"{domainName}._report._dmarc.{mailboxDomain}";
    var record = await QueryTxtAsync(authorizationName, cancellationToken).ConfigureAwait(false);
    return record is null
        ? new DmarcAuthorizationCheckResult(DmarcAuthorizationCheckStatus.Missing, $"No TXT record found at {authorizationName}")
        : new DmarcAuthorizationCheckResult(DmarcAuthorizationCheckStatus.Ok, null);
}
```

(`QueryTxtAsync` is the checker's existing private helper — reused as-is.)

### SPF

New `ISpfDnsChecker`/`SpfDnsChecker` (`src/DotMarc/Dns/`), same DoH-querying shape as `DmarcDnsChecker`/`TlsrptDnsChecker`:

```csharp
public async Task<SpfCheckResult> CheckAsync(string domainName, CancellationToken cancellationToken)
{
    var records = await QueryAllTxtAsync(domainName, cancellationToken).ConfigureAwait(false); // all TXT records, not just the first
    var spfRecords = records.Where(r => r.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)).ToList();

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
```

Needs a `QueryAllTxtAsync` variant (returning every TXT answer at the name, not just the first) since detecting "multiple SPF records" requires seeing all of them — `DmarcDnsChecker`/`TlsrptDnsChecker`'s existing `QueryTxtAsync` helpers only return the first match, so this is a new private helper on `SpfDnsChecker`, not a shared/reused one (matching this codebase's established "small, independent DNS-over-HTTPS callers over a shared abstraction" precedent, per `DmarcTxtLookup`'s own doc comment).

### MX

New `IMxDnsChecker`/`MxDnsChecker` (`src/DotMarc/Dns/`) — does its own raw MX query rather than reusing `IMxHostsLookup` (which trims/dedupes for a different purpose — pre-filling MTA-STS policy tags — and doesn't preserve the distinction between a real MX target and an RFC 7505 null MX):

```csharp
public async Task<MxCheckResult> CheckAsync(string domainName, CancellationToken cancellationToken)
{
    var mxAnswers = await QueryMxAsync(domainName, cancellationToken).ConfigureAwait(false); // (Preference, Exchange) pairs, Exchange NOT trimmed of trailing dot yet

    if (mxAnswers.Count == 0)
    {
        return new MxCheckResult(MxCheckStatus.MissingRecord, $"No MX record found at {domainName}");
    }
    if (mxAnswers.Count == 1 && mxAnswers[0].Exchange == ".")
    {
        // RFC 7505 null MX — an explicit, intentional "this domain does not accept mail" policy.
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
```

`ResolvesAsync` queries `type=A` for the host and returns whether any answer came back.

### DKIM

New `IDkimDnsChecker`/`DkimDnsChecker` (`src/DotMarc/Dns/`), takes the configured selector list (no selector discovery/guessing):

```csharp
public async Task<DkimCheckResult> CheckAsync(string domainName, IReadOnlyList<string> selectors, CancellationToken cancellationToken)
{
    // Caller (PollingService) already short-circuits to NotConfigured when selectors is empty —
    // this method is only ever called with at least one selector.
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
```

## PollingService changes

Four new independent check cycles, each following the exact existing `RunDmarcCheckCycleAsync` shape (own advisory lock, 24h staleness cutoff, loop with try/catch-continue over an `internal static` single-domain method, save once):

- `RunDmarcAuthorizationCheckCycleAsync` / `RunSingleDmarcAuthorizationCheckAsync` — new lock key `84_200_011`.
- `RunSpfCheckCycleAsync` / `RunSingleSpfCheckAsync` — new lock key `84_200_013`.
- `RunMxCheckCycleAsync` / `RunSingleMxCheckAsync` — new lock key `84_200_015`.
- `RunDkimCheckCycleAsync` / `RunSingleDkimCheckAsync` — new lock key `84_200_017`.

`RunSingleDkimCheckAsync` short-circuits before calling the checker:

```csharp
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

All four new cycles are dispatched from `ExecuteAsync`'s main loop exactly like the existing three (each in its own try/catch, logging and continuing to the next interval on failure — never letting one check's failure block the others).

Each new `internal static RunSingle*Async` method is directly reusable from `DomainDetail.razor`'s manual "recheck now" buttons, following the exact pattern already established for `RunSingleDmarcCheckAsync`/`RunSingleTlsrptCheckAsync`/`RunSingleMtaStsCheckAsync`.

## UI changes

### Overview tab: consolidated health checklist

Replace the current two `MudPaper` blocks (DMARC status, TLS reporting status) with one `MudPaper` containing a new reusable component, `DomainHealthCheckRow.razor` (`src/DotMarc/Components/Shared/`), invoked once per check:

```razor
<MudPaper Class="pa-4 mt-4" Elevation="1">
    <MudText Typo="Typo.subtitle1" Class="mb-2">Domain health</MudText>
    <DomainHealthCheckRow Title="DMARC record" StatusColor="..." StatusLabel="..." CheckedUtc="..." Detail="...">
        <ActionContent>...push button or recheck button...</ActionContent>
    </DomainHealthCheckRow>
    <DomainHealthCheckRow Title="DMARC authorization record" ... />
    @if (!string.IsNullOrWhiteSpace(GraphOptions.Value.TlsrptMailboxAddress))
    {
        <DomainHealthCheckRow Title="TLS reporting record" ... />
    }
    <DomainHealthCheckRow Title="MTA-STS" ...>
        <ActionContent><MudLink OnClick="@(() => NavigateToTab("mta-sts"))">View details</MudLink></ActionContent>
    </DomainHealthCheckRow>
    <DomainHealthCheckRow Title="SPF" ... />
    <DomainHealthCheckRow Title="MX" ... />
    <DomainHealthCheckRow Title="DKIM" ...>
        <ActionContent><MudButton OnClick="OpenDkimSelectorsDialogAsync">Configure selectors</MudButton></ActionContent>
    </DomainHealthCheckRow>
</MudPaper>
```

`DomainHealthCheckRow` lays out one row as a CSS grid (title | status chip | last-checked | detail-and-action), not a literal `<table>` — a real `MudTable` with an `Items`-bound `RowTemplate` assumes homogeneous rows, but every row here has a different action affordance (push button, tab link, configure-selectors button, or nothing), so a small presentational component composed seven times is simpler than fighting a heterogeneous table. This mirrors the existing `DomainMtaStsPanel.razor` precedent of a focused, single-purpose shared component.

Each `DomainHealthCheckRow` invocation reuses this feature's existing per-check status-presentation classes for `StatusColor`/`StatusLabel` (`DmarcStatusPresentation`, the new `DmarcAuthorizationStatusPresentation`/`SpfStatusPresentation`/`MxStatusPresentation`/`DkimStatusPresentation`, `TlsrptStatusPresentation`, `MtaStsStatusPresentation` — same `GetColor`/`GetLabel` static-method shape as every existing one).

**Existing push buttons relocate, unchanged in behavior**: `PushDmarcRecordAsync`, `PushDmarcAuthorizationRecordAsync`, `PushTlsrptRecordAsync` (all already implemented) move from their current inline conditional blocks into the DMARC/DMARC-authorization/TLSRPT rows' `ActionContent`. `RecheckDmarcAsync`/`RecheckTlsrptAsync` (already implemented) move similarly — each row gets both a recheck (refresh icon) and, when its status calls for one, a push button, side by side in `ActionContent`.

**New**: `RecheckSpfAsync`/`RecheckMxAsync`/`RecheckDkimAsync` on `DomainDetail.razor`, following the exact existing `RecheckDmarcAsync` shape (fresh tracked context, call the new `PollingService.RunSingleSpfCheckAsync`/etc., save, mirror fields onto `_domain`).

**New**: a small `ConfigureDkimSelectorsDialog.razor` (`src/DotMarc/Components/Dialogs/`) — a text field (one selector per line, mirroring `DomainMtaStsPanel`'s existing MX-hosts textarea pattern), Save/Cancel. Saving persists `Domain.DkimSelectors` via a new `DomainManagementService.SetDkimSelectorsAsync` and immediately triggers a recheck (matching the "enable MTA-STS" flow's immediate-push-after-save pattern) so the row updates without waiting for the next scheduled cycle.

### Authorization gating

Recheck buttons: `DomainsEdit` (matching every existing recheck button). Push buttons: `DomainsEdit` (unchanged, matching existing dmarc/dmarc-auth/tlsrpt buttons). DKIM's "Configure selectors" button: `DomainsEdit` (it's a config-editing action, same tier as everything else on this tab). SPF/MX rows get a recheck button but no push button (per the Non-goals section).

## Testing

- `CheckAuthorizationAsync` (new test cases added to the existing `test/DotMarc.Tests/Dns/DmarcDnsCheckerTests.cs`): NotApplicable when mailbox domain matches, Missing when absent, Ok when present — using the existing `FakeHttpMessageHandler` pattern.
- `SpfDnsCheckerTests.cs` (new): missing, single Ok, multiple records, wrong prefix.
- `MxDnsCheckerTests.cs` (new): missing, single resolving host Ok, null MX Ok, unresolvable target.
- `DkimDnsCheckerTests.cs` (new): missing selector, present selector without p=, present and valid, multiple selectors mixed.
- `PollingServiceTests.cs`: extend with cases for each new cycle's staleness/lock/save behavior, matching existing DMARC/TLSRPT cycle test coverage.
- Demo data (`DemoDataGenerator.cs`/`DemoDataset.cs`/`DemoDataSeeder.cs`): extend the seeded domains to exercise a representative spread of the new statuses (at least one domain per new failure mode), matching how MTA-STS's demo domains already cover its full status range.

## Migration path for existing data

No data migration script needed. `DmarcCheckStatus.MissingAuthorizationRecord` stays defined in the enum (existing rows may still hold it in the database) but the checker stops emitting it going forward. Any domain currently sitting at that value self-corrects on its next scheduled DMARC check: `DmarcCheckStatus` moves to `Ok` (assuming the own record was actually fine) and the new `DmarcAuthorizationCheckStatus` field is populated with the accurate independent result. `DkimSelectors` defaults to an empty list for every existing domain, which `RunSingleDkimCheckAsync`'s short-circuit reads as `NotConfigured` — a neutral, non-alarming default rather than a false failure.
