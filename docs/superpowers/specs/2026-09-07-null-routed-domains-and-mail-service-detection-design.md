# Null-Routed Domain Detection & Mail Service Detection Design

## Problem

dotMARC currently treats "no DMARC reports for 2+ days" as always a problem (`MissedReport` alert) for every monitored domain. That's wrong for a domain that's been deliberately configured to never send mail — a "parked" or "null-routed" domain. For that domain, silence is the *healthy* state, and the thing actually worth an alert is the opposite: a DMARC aggregate report arriving at all, since that means mail claiming to be from a domain that's supposed to send nothing just went out — legitimate traffic someone forgot to account for, or a spoofing attempt.

Separately, dotMARC has no way to show which mail service(s) a domain actually uses (Google Workspace, Microsoft 365, Mailchimp, etc.) — useful at-a-glance context when reviewing a domain, and a foundation for pre-filling DKIM selector configuration instead of requiring the admin to already know them.

## Goals

- Detect RFC 7505 null MX and the SPF "no senders authorized" convention (`v=spf1 -all`) as first-class, distinguishable check states (not folded into a generic `Ok`).
- Define "null-routed" (per the domain's own SPF record) automatically — no manual toggle — and use it to invert alerting: suppress the missing-report alert, and raise a new alert the moment a report actually arrives.
- Show null-routed status clearly on the Dashboard and skip the "Missing" report-status indicator for those domains, since no reports is the expected state.
- Detect and badge known mail services (inbox provider via MX, sending services via SPF `include:`) on the domain detail Overview tab.
- Pre-fill the "Configure DKIM selectors" dialog with a provider-appropriate suggestion when the domain's inbox provider is recognized, editable/clearable by the admin — never applied without their action.

## Non-goals

- No automatic p=reject enforcement or computed check for DMARC policy strength on null-routed domains. DMARC stays exactly as it is today (own record + rua=dotMARC, mandatory, unchanged mechanism) — that's the channel the new alert rides on. A short static help note near the null-routed indicator mentions that `p=reject` is the recommended policy for a parked domain; this is copy, not a tracked check.
- No manual "mark as null-routed" override. Fully automatic per the approved design — see Data model changes for why no new stored flag is even needed.
- Mail service detection is informational only — no push/remediation action attached to a detected badge.
- DKIM selector auto-suggestion is scoped to the five inbox providers with a reliable, universal MX-pattern-to-selector convention (see DKIM section) — not to sending-only services, whose DKIM setup is per-account and can't be usefully pre-filled.

## Data model changes

**No new persisted field for "null-routed."** It's a pure function of the existing `Domain.SpfCheckStatus` field (`IsNullRouted ⟺ SpfCheckStatus == SpfCheckStatus.NullSpf`) — every place that needs it (AlertingService, Dashboard, DomainDetail) computes it directly from that field rather than keeping a second value in sync.

Two new enum members, both `HasConversion<string>()` already in place for their columns (`DotMarcDbContext.cs`), so no migration is needed beyond what EF generates for the enum's new string value — no schema change, no new column:

`src/DotMarc/Data/MxCheckStatus.cs` — add `NullMx` after `Ok`:
```csharp
public enum MxCheckStatus
{
    NotChecked,
    Ok,
    NullMx,
    MissingRecord,
    UnresolvableTarget
}
```

`src/DotMarc/Data/SpfCheckStatus.cs` — add `NullSpf` after `Ok`:
```csharp
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

## Checker changes

### MxDnsChecker — distinguish null MX from a generic pass

`src/DotMarc/Dns/MxDnsChecker.cs`'s existing null-MX branch currently returns `MxCheckStatus.Ok`:
```csharp
if (mxAnswers.Count == 1 && mxAnswers[0].Exchange == ".")
{
    return new MxCheckResult(MxCheckStatus.Ok, "Explicit null MX (RFC 7505) — this domain intentionally does not accept mail.");
}
```
Change the status to `MxCheckStatus.NullMx` (detail text unchanged). `MxStatusPresentation.GetColor`/`GetLabel` need a new arm — still green/healthy, just a distinct label:
```csharp
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
```
`test/DotMarc.Tests/Dns/MxDnsCheckerTests.cs`'s existing `CheckAsync_ReturnsOk_WhenNullMxIsPublished` test needs its assertion updated from `MxCheckStatus.Ok` to `MxCheckStatus.NullMx`.

### SpfDnsChecker — detect the null pattern

`src/DotMarc/Dns/SpfDnsChecker.cs`'s `CheckAsync`, after confirming exactly one SPF record exists (`spfRecords.Count == 1`), currently returns `Ok` unconditionally. Add a check for the null pattern before that: the record, with `v=spf1` and surrounding whitespace stripped, has exactly one remaining token, and that token is `-all` (case-insensitive):

```csharp
if (spfRecords.Count > 1)
{
    return new SpfCheckResult(SpfCheckStatus.MultipleRecords, $"{domainName} has {spfRecords.Count} SPF records — RFC 7208 requires exactly one");
}

var mechanisms = spfRecords[0]["v=spf1".Length..]
    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (mechanisms.Length == 1 && string.Equals(mechanisms[0], "-all", StringComparison.OrdinalIgnoreCase))
{
    return new SpfCheckResult(SpfCheckStatus.NullSpf, $"{domainName} publishes a null SPF record (v=spf1 -all) — no senders are authorized to send mail as this domain.");
}

return new SpfCheckResult(SpfCheckStatus.Ok, null);
```
(The `spfRecords[0]["v=spf1".Length..]` slice is safe because `spfRecords` was already filtered to records `StartsWith("v=spf1", ...)` earlier in the method.)

`SpfStatusPresentation.GetColor`/`GetLabel` need the same treatment as MX's:
```csharp
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
```

New test cases for both: `test/DotMarc.Tests/Dns/MxDnsCheckerTests.cs`'s existing null-MX test gets its assertion updated (see above); `test/DotMarc.Tests/Dns/SpfDnsCheckerTests.cs` gets a new test asserting `v=spf1 -all` → `NullSpf`, and a case confirming a record with `-all` plus a real mechanism (e.g., `v=spf1 include:_spf.google.com -all`) still returns `Ok`, not `NullSpf` — the null pattern requires *only* `-all`, nothing else.

## Alerting changes

`src/DotMarc/Notifications/AlertingService.cs`:

**Suppress `MissedReport` for null-routed domains.** `CheckPinnedDomainsAsync`'s loop currently checks every `IsMonitored` domain unconditionally. Add a null-routed branch before the existing check, which also resolves any stale `MissedReport` alert left over from before the domain became null-routed:

```csharp
foreach (var domain in domains)
{
    if (domain.SpfCheckStatus == SpfCheckStatus.NullSpf)
    {
        // Null-routed (SPF v=spf1 -all): no reports is the expected, healthy state, not a
        // problem — resolve any pre-existing alert from before the domain became null-routed
        // and skip the missing-report check entirely for it.
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

**New alert when a report arrives for a null-routed domain.** New method on `IAlertingService`:
```csharp
Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, CancellationToken cancellationToken = default);
```
Implementation, following the exact shape of `HandleTlsrptReportAsync`'s settings-check-then-`EnsureAlertAsync` pattern:
```csharp
public async Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, CancellationToken cancellationToken = default)
{
    await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
    var settings = await NotificationSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
    if (!settings.Enabled)
    {
        return;
    }

    var message = $"'{domainName}' is marked null-routed (SPF v=spf1 -all — no authorized senders) but a DMARC aggregate report just arrived showing mail activity. This may be legitimate traffic that needs accounting for, or a spoofing attempt.";
    await EnsureAlertAsync(db, settings, domainName, "UnexpectedActivityOnNullRoutedDomain", "Warning", "Unexpected mail activity on a null-routed domain", message, cancellationToken).ConfigureAwait(false);
}
```
`AlertEvent.AlertType` is a plain string column (matching the existing `"MissedReport"`/`"TlsrptFailure"` literals) — no enum to extend.

**Wire it into report processing.** `src/DotMarc/Ingestion/PollingService.cs`'s `ProcessMessageAsync`, right after the existing `_alertingService.ResolveDomainAlertAsync(domain.Name, cancellationToken)` call (which already runs on every successfully-stored report, duplicate or not — `ResolveAlertAsync` is a safe no-op when there's nothing active to resolve):
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
`domain` here is the entity `StoreReportAsync` loaded fresh from `context.Domains` earlier in the same call chain, so `SpfCheckStatus` reflects its latest persisted value from the independent SPF check cycle — no extra query needed. `EnsureAlertAsync`'s existing cooldown logic already prevents duplicate-alert spam if this fires more than once in quick succession (e.g. a retried message).

## Dashboard changes

`src/DotMarc/Reporting/DashboardSummary.cs`: add `d.SpfCheckStatus` to `DashboardDomainRow` (new parameter, appended after the existing `MtaStsStatus` field) and thread it through `Build`'s `Select`. Also update the existing "Missing" report-status computation to skip null-routed domains, mirroring the alerting change:
```csharp
var isNullRouted = d.SpfCheckStatus == SpfCheckStatus.NullSpf;
var missingReport = d.IsMonitored && !isNullRouted && (d.LastReportReceivedUtc is null || d.LastReportReceivedUtc < DateTimeOffset.UtcNow.AddDays(-2));
```

`src/DotMarc/Components/Pages/Dashboard.razor`: a small "Null-routed" chip next to the domain name cell, shown only when `context.SpfCheckStatus == SpfCheckStatus.NullSpf`:
```razor
<MudTd @onclick="@(() => Navigation.NavigateTo($"/domains/{Uri.EscapeDataString(context.Name)}"))" Style="cursor:pointer">
    @context.Name
    @if (context.SpfCheckStatus == SpfCheckStatus.NullSpf)
    {
        <MudChip T="string" Color="Color.Info" Size="Size.Small" Class="ml-2">Null-routed</MudChip>
    }
</MudTd>
```

## Mail service detection

New, independent checker in `src/DotMarc/Dns/` — following the same "small, independent DNS-over-HTTPS caller, no shared abstraction" convention as every other checker in this codebase:

```csharp
public enum DetectedMailServiceKind { Inbox, Sending }
public sealed record DetectedMailService(string ProviderName, DetectedMailServiceKind Kind);

public interface IMailServiceDetector
{
    Task<List<DetectedMailService>> DetectAsync(string domainName, CancellationToken cancellationToken);
}
```

`MailServiceDetector` does its own raw MX query (matching `MxDnsChecker`'s exact query/parse shape) and its own raw TXT query for the SPF record (matching `SpfDnsChecker`'s), then matches each against a static lookup table by hostname suffix. No shared code with `MxDnsChecker`/`SpfDnsChecker` — same precedent as `MxDnsChecker` not reusing `IMxHostsLookup`.

**Inbox providers (matched against MX exchange hostnames):**

| Provider | MX suffix match |
|---|---|
| Microsoft 365 | `.mail.protection.outlook.com` |
| Google Workspace | `aspmx.l.google.com` (covers `alt1-4.aspmx.l.google.com` too, since suffix match) |
| Zoho Mail | `.zoho.com`, `.zohomail.com`, `.zohomail.eu`, `.zohomail.in` |
| Fastmail | `.messagingengine.com` |
| ProtonMail | `.protonmail.ch` |

**Sending services (matched against SPF `include:` mechanism hostnames):**

| Provider | SPF include hostname suffix match |
|---|---|
| Google Workspace | `_spf.google.com` |
| Microsoft 365 | `spf.protection.outlook.com` |
| Zoho | `zoho.com`, `zoho.eu` |
| Mailchimp | `servers.mcsv.net` |
| SendGrid | `sendgrid.net` |
| Amazon SES | `amazonses.com` |
| Salesforce | `_spf.salesforce.com` |

A domain can match zero, one, or several entries across both tables (e.g., Google Workspace as inbox provider *and* Mailchimp as a sending service both match on the same domain — two separate badges).

**UI**: `src/DotMarc/Components/Pages/DomainDetail.razor`'s Overview tab, a small row of badges below the "Domain health" heading (or integrated into that same header row), one per detected service, `Kind` distinguishing an inbox badge from a sending badge (e.g. different `MudChip` `Color` or an icon prefix — exact visual treatment is an implementation detail for the plan). Fetched once in `OnInitializedAsync` alongside the other checks already loaded there.

## DKIM selector auto-suggestion

`src/DotMarc/Components/Dialogs/ConfigureDkimSelectorsDialog.razor` currently starts with an empty (or already-configured) selector list. When opening the dialog for a domain with NO selectors configured yet, pre-fill `_selectorsText` with the suggestion for the detected inbox provider (reusing `IMailServiceDetector`'s inbox-provider table — same five providers as above, since those are the ones with a reliable, universal, non-account-specific selector convention):

| Provider | Suggested selector(s) |
|---|---|
| Microsoft 365 | `selector1`, `selector2` |
| Google Workspace | `google` |
| Zoho Mail | `zoho1` |
| Fastmail | `fm1`, `fm2`, `fm3` |
| ProtonMail | `protonmail2`, `protonmail3` |

This is a pre-fill only — the admin can accept, edit, or clear it before saving, exactly like any other pre-populated form field. If no inbox provider is detected, or selectors are already configured, the dialog opens exactly as it does today (unprefilled or showing the existing selectors).

## Testing

- `MxDnsCheckerTests.cs`: update the existing null-MX test's expected status to `NullMx`.
- `SpfDnsCheckerTests.cs`: new test for `v=spf1 -all` → `NullSpf`; new test confirming `v=spf1 include:_spf.google.com -all` (a real mechanism present alongside `-all`) → `Ok`, not `NullSpf`.
- `test/DotMarc.Tests/Notifications/AlertingServiceTests.cs`: new test confirming `CheckPinnedDomainsAsync` skips the missing-report check and resolves any existing `MissedReport` alert for a domain with `SpfCheckStatus == NullSpf`; new test for `FlagUnexpectedActivityForNullRoutedDomainAsync` creating an `UnexpectedActivityOnNullRoutedDomain` alert.
- `PollingServiceTests.cs` or a dedicated report-processing test file: confirm `ProcessMessageAsync` calls `FlagUnexpectedActivityForNullRoutedDomainAsync` when storing a report for a null-routed domain, and does not call it for a normal domain.
- New `MailServiceDetectorTests.cs`: one test per lookup-table entry (both tables), confirming the exact hostname match; a test confirming multiple simultaneous matches (inbox + sending) both surface; a test confirming no match returns an empty list.
- `DashboardSummaryTests.cs`: new test confirming a null-routed domain's row never reads `"Missing"` regardless of `LastReportReceivedUtc`.

## Migration path for existing data

No migration needed — both new enum members persist via the same `HasConversion<string>()` already configured for their columns, and every existing domain simply gets reclassified as `NullMx`/`NullSpf` (or not) on its next regularly-scheduled MX/SPF check, same as any other status change. No existing row needs backfilling.
