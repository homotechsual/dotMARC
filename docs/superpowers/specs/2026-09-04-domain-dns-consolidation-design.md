# Domain DNS Management Consolidation Design

## Problem

A domain's DNS health today is split across three separate places: the domain detail page's
Overview tab (DMARC and TLS reporting status, with an inline push button), the domain detail
page's MTA-STS tab (status only — no controls at all, just a link out), and the standalone
`/mta-sts` page (the only place to actually enable, configure, and push MTA-STS for a domain).
Managing one domain's DNS setup means jumping between all three.

Separately, today's live debugging of the DNS push flow (see
`2026-09-04-mta-sts-azure-domain-verification-design.md` and that day's session) surfaced two
concrete gaps in the push mechanism itself:

- MTA-STS's push is create-only. It never checks whether a conflicting record already exists, so a
  domain with a stale or pre-existing `mta-sts.<domain>` CNAME just gets a raw, unhelpful "already
  exists" error from the provider instead of an offer to fix it.
- DMARC's existing-record handling only engages when dotMARC's own *cached* status already says
  "Misconfigured" — proven stale in practice (a record existed that dotMARC's push flow didn't
  expect). TLSRPT has no existing-record handling at all, even though its own status can be
  "Misconfigured" too. Neither ever detects the case where the existing record is a **CNAME
  delegating elsewhere** (e.g. to a third-party DMARC monitoring service) rather than a plain TXT
  record with a stale value — a meaningfully different, higher-stakes situation than "the value is
  wrong."

## Goals

- One page per domain (Domain Detail) for checking and fixing all of that domain's DNS records —
  DMARC, TLS reporting, and MTA-STS — with no separate page to visit for any of them.
- Enabling/fixing a record and pushing the resulting DNS change becomes one action, not two
  separate steps across two page visits.
- Every push target (MTA-STS, DMARC, TLSRPT) does a live check for a conflicting existing record
  before pushing, regardless of what status is cached, and offers a confirm-and-replace step when
  one is found — closing the exact gap that caused today's MTA-STS push to fail with a raw
  provider error instead of a usable prompt.
- A third-party CNAME delegation (DMARC/TLSRPT only) is detected and surfaced distinctly from an
  ordinary "value differs" conflict, naming the delegation target and stating plainly that
  proceeding removes it, before still allowing the admin to confirm and replace it if that's
  genuinely what they want.

## Non-goals

- No change to the underlying push mechanism itself (OAuth flow, popup, scopes, provider
  implementations) — all of that was fixed and confirmed working today. This design only changes
  what happens *before* a push is initiated (the existing-record check) and *where* the controls
  live.
- No change to MTA-STS's own record type expectations — a CNAME at `mta-sts.<domain>` is normal
  and expected there; the delegation-detection behavior in this design applies only to DMARC and
  TLSRPT, which normally expect a bare TXT record.
- No database schema changes.
- Auto-detecting and blocking *every* possible DNS misconfiguration is out of scope — this design
  only extends the specific "does a conflicting record already exist" check that today's MTA-STS
  bug exposed as missing.

## Architecture

### 1. Consolidation: one page per domain

`Components/Pages/DomainDetail.razor`'s MTA-STS tab (currently `MudTabPanel Text="MTA-STS"` at
line 115, showing only a status chip and a "Manage MTA-STS settings" link to `/mta-sts`) gets a
new child component, `Components/Shared/DomainMtaStsPanel.razor`, replacing that link with the
full enable/configure/push experience — extracted into its own file rather than growing
`DomainDetail.razor` further, matching this repo's existing pattern of keeping page files focused
(the same reasoning that put `UnsavedChangesGuard` and `ConfirmDnsRecordPushDialog` in their own
files).

`Components/Pages/ManageMtaSts.razor`, its `/mta-sts` route, and its nav link
(`Components/Layout/MainLayout.razor:38`, `<MudMenuItem Href="/mta-sts" ...>`) are all removed.
The `MtaStsManage` policy check that gated that link moves to gate the new panel's controls
instead — a viewer with only `MtaStsView` still sees the status; only `MtaStsManage` sees the
enable/configure/push controls, matching how `AuthorizeView Policy="MtaStsManage"` already gates
the link today.

### 2. Simplified enable flow

`DomainMtaStsPanel` renders one of two states:

- **Not yet enabled:** a single "Enable MTA-STS" button. Clicking it: fetches MX hosts
  automatically (reusing the existing `IMxHostsLookup`, no manual "fetch" step required first),
  saves via the existing `DomainManagementService.SetMtaStsConfigAsync` with `MtaStsMode.Testing`
  and `MtaStsMaxAgeSeconds = 604_800` (both already the entity's own defaults — `Domain.cs`'s
  `MtaStsMode` enum lists `Testing` first specifically so it's the safe default, and
  `MtaStsMaxAgeSeconds` already defaults to `604_800`), then immediately runs the live-check +
  push flow described in section 4.
- **Already enabled:** the status chip and detail text exactly as today, plus an "Advanced"
  expander (MudBlazor's `MudExpansionPanel` or similar collapsed-by-default element) containing
  the Mode select, max-age field, and MX hosts text field — the same three inputs
  `ManageMtaSts.razor`'s table row has today, just presented as a single-domain form instead of a
  table row. Saving from inside the expander behaves as it does today: no automatic push trigger,
  since an already-enabled domain's config edit isn't the same action as the initial enable.
  Below the status, when applicable, the same push button today's page shows (gated on
  `MtaStsStatus.PendingDns`) — but see section 4 for what "push" now does differently.

### 3. Combined enable/fix → push flow

For MTA-STS specifically: the "Enable" button's click handler runs save, then immediately invokes
the same live-check-then-push logic described in section 4 (as if the push button had also just
been clicked), rather than requiring a second visit after the page re-renders with a push button.

DMARC and TLSRPT don't have a separate "enable" step to combine in the first place — their
Misconfigured/MissingOwnRecord status comes from dotMARC's own polling cycle, not a user action,
so "Push via your DNS provider" is already the one and only click an admin takes to act on it.
What changes for them is what that one click now does: always the live-check flow in section 4
first, never a direct push straight to the popup.

### 4. Generalized live-check + confirm dialog

A new pair, `IMtaStsCnameLookup`/`MtaStsCnameLookup` (in `src/DotMarc/MtaSts/`), mirrors the
existing `IDmarcTxtLookup`/`ITlsrptTxtLookup` pattern exactly: a plain DNS-over-HTTPS query against
Cloudflare's public resolver for `mta-sts.<domain>`'s current CNAME target, no provider API
involved. Its `LookupAsync` returns `Task<string?>` — the current CNAME target, or null if nothing
resolves.

`DmarcTxtLookup`/`TlsrptTxtLookup` gain the CNAME-delegation detection from section 5 (their
return type changes — see below).

Each of the three push handlers (`DomainMtaStsPanel`'s enable/push action,
`DomainDetail.razor`'s `PushDmarcRecordAsync`, `PushTlsrptRecordAsync`) follows the same shape
before ever calling `OpenDnsPushPopupAsync`:

1. Compute the proposed value (MTA-STS: the configured hosting hostname; DMARC/TLSRPT: via the
   existing `DmarcRuaMerge`/`TlsrptRuaMerge` merge logic against whatever's currently live).
2. Run the live lookup for that record.
3. If nothing exists, or the existing value already matches the proposed one: push directly, no
   dialog — this is the common case and stays a single click, matching today's DMARC
   "MissingOwnRecord" behavior.
4. If something exists and differs: show the confirm dialog (section 5) and proceed only if
   confirmed.

`Components/Dialogs/ConfirmDnsRecordPushDialog.razor` becomes record-type-agnostic: its title
becomes a parameter (`RecordDescription`, e.g. "DMARC record", "MTA-STS CNAME") instead of the
hardcoded "Push DMARC record fix", and its body text's hardcoded `_dmarc.@DomainName` becomes a
`RecordName` parameter. Existing/proposed value display stays as-is.

### 5. Detecting third-party CNAME delegation (DMARC/TLSRPT only)

`DmarcTxtLookup`/`TlsrptTxtLookup` currently discard the DNS answer chain, keeping only the first
`Type == 16` (TXT) entry — which is also the entry a plain TXT query returns even when a CNAME hop
happened first, since resolvers follow CNAMEs transparently. Both queries' raw DoH JSON response
already contains any CNAME hop as a `Type == 5` entry in the same `Answer` array (confirmed
directly against Cloudflare's public resolver during today's investigation into
`homotechsual.dev`'s DMARC record).

Both lookups' return type changes from `Task<string?>` to `Task<DnsRecordLookupResult>`:

```csharp
public sealed record DnsRecordLookupResult(string? DirectValue, string? DelegatedToCname);
```

`DirectValue` is today's existing return value (null if nothing resolves at all).
`DelegatedToCname` is set (and `DirectValue` reflects whatever the CNAME chain ultimately
resolved to, same as today) when a `Type == 5` entry precedes the final TXT answer — i.e. the
record at the expected name is itself a CNAME, not a direct TXT record.

When `DelegatedToCname` is set, the confirm dialog (section 4) shows a distinct variant: instead
of "current value / will be replaced with", it states that this record is currently delegated via
CNAME to the named target, and that proceeding removes that delegation — worded plainly enough
that clicking through is an informed choice, not an easy-to-miss detail buried in a value diff.
The dialog still ends in the same Cancel/Apply choice; this design does not block replacement, per
the approved direction — it makes the stakes explicit first.

### 6. Server-side: MTA-STS gains a real merge path

`Program.cs`'s `/dns-push/{provider}/callback` MTA-STS branch currently always builds a
`DnsRecordChangeKind.Create` change for the CNAME (the exact gap that produced today's raw
"already exists" provider error). It gains the same shape DMARC/TLSRPT already have: call
`IMtaStsCnameLookup` server-side too, and build a `Merge`-kind change
(`DnsRecordChange(DnsRecordChangeKind.Merge, "CNAME", $"mta-sts.{domain.Name}", hostingHostname,
existingCname, domain.Name)`) when a CNAME already exists there, `Create` otherwise. This mirrors
the existing DMARC/TLSRPT branches' shape exactly, just for a CNAME instead of a TXT record — the
provider-level push code (`CloudflareDnsPushProvider`/`AzureDnsPushProvider`) already handles
`Merge` generically for any record type, so no provider-level change is needed here.

The client-side live-check (section 4) and this server-side check are intentionally redundant, the
same way DMARC's already are today: the client-side check exists purely to decide whether to show
the confirm dialog; the server-side check is what actually determines Create vs. Merge for the
real push, and is authoritative regardless of what the client saw (the two can disagree if DNS
changed in the few seconds between the client's check and the OAuth round-trip completing — the
server's view wins, since it's the one actually writing the record).

## File Structure

- New: `Components/Shared/DomainMtaStsPanel.razor` — per-domain MTA-STS enable/configure/push UI.
- New: `MtaSts/IMtaStsCnameLookup.cs`, `MtaSts/MtaStsCnameLookup.cs` — public DNS CNAME lookup.
- Modify: `Components/Dialogs/ConfirmDnsRecordPushDialog.razor` — generalized parameters, plus the
  delegation-warning variant.
- Modify: `Components/Pages/DomainDetail.razor` — MTA-STS tab hosts the new panel; DMARC/TLSRPT
  push handlers gain the live-check step before calling `OpenDnsPushPopupAsync`.
- Modify: `DnsPush/DmarcTxtLookup.cs`, `DnsPush/IDmarcTxtLookup.cs`, `DnsPush/TlsrptTxtLookup.cs`,
  `DnsPush/ITlsrptTxtLookup.cs` — return `DnsRecordLookupResult` instead of `string?`.
- Modify: `Program.cs` — MTA-STS branch of `/dns-push/{provider}/callback` gains the merge path;
  all three branches' call sites for the two changed lookup interfaces adjust to the new return
  shape (currently only the `/callback` endpoint calls them).
- Delete: `Components/Pages/ManageMtaSts.razor`.
- Modify: `Components/Layout/MainLayout.razor` — remove the `/mta-sts` nav link.
- Docs: `website/docs/mta-sts.mdx`, `website/docs/getting-started.mdx` — replace references to a
  separate Manage MTA-STS page with the domain-detail-page flow.

## Testing

- `MtaStsCnameLookup`: no existing test infrastructure exists for its two DNS-lookup siblings
  (`DmarcTxtLookup`/`TlsrptTxtLookup` have none today, per this codebase's established convention
  of not adding HTTP-call mocking scaffolding for these small DoH callers) — matches that
  convention, verified manually against live DNS the same way this session's earlier work was.
- The CNAME-delegation detection logic (parsing `Type == 5` vs `Type == 16` from a raw DoH JSON
  answer array) is pure parsing logic with no network dependency once given a response body — this
  part is testable with zero mocking (construct a JSON string with both a CNAME and TXT answer
  entry, assert `DelegatedToCname` is populated correctly) and should get unit tests, unlike the
  network-calling wrapper around it.
- The live-check-before-push decision logic (nothing exists / matches / differs) shared across the
  three push handlers is also pure logic once given a proposed value and a lookup result — worth
  extracting into a small, independently testable helper rather than duplicating the branching
  three times inline.
- `DomainMtaStsPanel` and the modified `DomainDetail.razor`/`ConfirmDnsRecordPushDialog.razor`:
  no Razor component test harness exists in this repo (confirmed earlier this session) — manual
  browser verification, per this project's established pattern for UI changes.
