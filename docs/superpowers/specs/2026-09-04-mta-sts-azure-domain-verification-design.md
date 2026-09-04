# MTA-STS Azure Domain Verification Design

## Problem

Azure Container Apps requires two DNS records before it will bind a custom
hostname, not one. dotMARC's Azure-hosted MTA-STS flow
(`AzureMtaStsHostProvisioner`) only ever asks for the first:

```txt
mta-sts.<domain> CNAME <hosting hostname>
```

The second, an ownership-verification TXT record at `asuid.mta-sts.<domain>`
whose value is the Container App's `customDomainVerificationId`, is never
surfaced anywhere. A domain whose CNAME resolves correctly sails past
`PendingDns` into `PendingCertificate`, where `EnsureProvisionedAsync`'s ARM
call to bind the custom domain fails with Azure's raw
`InvalidCustomHostNameValidation` error. The domain lands in
`MtaStsStatus.Failed` with that raw JSON as the only clue, and — because the
CNAME push button in `ManageMtaSts.razor` only renders while
`Status == PendingDns` — there is no push action left to retry from either.

This only affects the Azure deployment target. `CaddyMtaStsHostProvisioner`
(self-hosted) never does DNS-based ownership verification; Caddy's
on-demand TLS issues purely off a successful `ask` callback, which itself
only succeeds once the CNAME has already resolved.

## Goals

- Surface the required `asuid.mta-sts.<domain>` TXT record proactively, in
  both dotMARC's own UI and its docs, before anyone hits the failure.
- Extend the existing DNS auto-push flow (Cloudflare / Azure DNS) to push
  this TXT record alongside the CNAME in one push action, for the `mta-sts`
  target when running on Azure.
- Replace the raw Azure ARM error with a dotMARC-authored message that names
  the exact record needed, when the specific ownership-validation failure
  is what went wrong.

## Non-goals

- No change to the self-hosted (Caddy) path — this whole gap doesn't exist
  there.
- No new `MtaStsStatus` state and no change to the state machine's existing
  transitions (`PendingDns → PendingCertificate → Active`, with `Failed` as
  a distinctly-surfaced retry state). The fix is entirely about what's
  shown/pushed and what the existing `Failed` detail message says — not
  about when transitions happen.
- No caching layer for the verification ID. It's fetched fresh (one cheap
  ARM `GET` already available on a resource the code loads anyway) on the
  infrequent, human-triggered paths that need it — a page load and a DNS
  push callback — not on `PollingService`'s poll loop.

## Architecture

### 1. Fetching the verification ID

`IMtaStsHostProvisioner` (`src/DotMarc/MtaSts/IMtaStsHostProvisioner.cs`)
gains one method:

```csharp
Task<string?> GetDomainVerificationIdAsync(CancellationToken cancellationToken);
```

- `CaddyMtaStsHostProvisioner` implements it as `Task.FromResult<string?>(null)`
  — same "nothing to do" shape as its existing `EnsureProvisionedAsync`/
  `TeardownAsync`.
- `AzureMtaStsHostProvisioner` implements it by calling its existing private
  `GetContainerAppAsync(cancellationToken)` and returning
  `containerApp.Data.CustomDomainVerificationId`. This is a Container-App-level
  property (fixed for the resource's lifetime, not per custom domain), so
  the same value is correct for every enabled domain on this deployment.

### 2. Surfacing it in dotMARC's UI

`ManageMtaSts.razor` injects `IMtaStsHostProvisioner` and fetches the
verification ID once during `OnInitializedAsync`, only when
`MtaStsOptions.Value.Provisioner` is `"Azure"` (mirrors the existing
Azure-only branch already in the page's `HelpAlert`). The existing
instructional text block is extended so the Azure branch shows both records
instead of just the CNAME:

```txt
mta-sts.<domain> CNAME <hosting hostname>
asuid.mta-sts.<domain> TXT <verification id>
```

This is unconditional — shown regardless of any individual domain's
current `MtaStsStatus` — because the value is the same for every domain and
someone should be able to read it before enabling anything, not just after
a failure.

### 3. Surfacing it in docs

- `website/docs/getting-started.mdx`, in the existing "MTA-STS policy
  hosting" section: a note that Azure-hosted deployments need this second
  record too, and where to find the value (the Manage MTA-STS page in-app,
  or `az containerapp show --query properties.customDomainVerificationId`
  for operators scripting it).
- `website/docs/mta-sts.mdx`, "Enabling a domain" section (the page linked
  from the in-app `HelpAlert`'s `DocsUrl`): a pointer that Azure-hosted
  instances require a second record, without hardcoding a value — this
  page is deployment-agnostic, so it defers to "check the Manage MTA-STS
  page for the exact value."

### 4. Extending DNS auto-push to cover both records

`IDnsPushProvider.ExchangeAndPushAsync` (`src/DotMarc/DnsPush/IDnsPushProvider.cs`)
changes its single `DnsRecordChange change` parameter to
`IReadOnlyList<DnsRecordChange> changes`:

```csharp
Task<DnsPushResult> ExchangeAndPushAsync(
    string code, string codeVerifier, string redirectUri,
    IReadOnlyList<DnsRecordChange> changes, CancellationToken cancellationToken);
```

Both implementations already branch on a single change's `Kind`/`RecordType`
to do the actual create-or-update work
(`CloudflareDnsPushProvider.CreateRecordAsync`/`UpdateExistingRecordAsync`,
`AzureDnsPushProvider.PushRecordAsync`). Each provider wraps that existing
per-change logic in a loop over `changes`, pushing them in list order and
returning the first non-`Pushed` result it hits; if every change succeeds,
it returns one `DnsPushResult(DnsPushOutcome.Pushed, null)` same as today.
The token exchange (`ExchangeCodeForTokenAsync` / MSAL's
`AcquireTokenByAuthorizationCode`) happens once per call, before the loop —
the authorization code is single-use, so this only works because both
records are pushed against the one resulting access token in the same
request, not two separate `/callback` round trips.

`Program.cs`'s `/dns-push/{provider}/callback` endpoint builds a
`List<DnsRecordChange>` instead of a single `change`. The `dmarc` and
`tlsrpt` branches are untouched (still exactly one change each). The
`mta-sts` branch always includes the CNAME change as it does today, and —
when `MtaStsOptions.Value.Provisioner` is `"Azure"` — also calls the new
`IMtaStsHostProvisioner.GetDomainVerificationIdAsync` and appends a second
`DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"asuid.mta-sts.{domain.Name}", verificationId, null)`.
If the verification ID comes back null or empty (the ARM call failed, or
this deployment's provisioner is misconfigured), the endpoint proceeds with
just the CNAME change rather than failing the whole push — same
fail-open-to-partial posture as the partial-failure case below.

### 5. Partial failure

If the CNAME push succeeds and the TXT push then fails (or the reverse),
`ExchangeAndPushAsync` returns that failure — the caller has no way to know
one of the two records already landed. This is an accepted, low-cost gap:
because step 2 now shows both records in the UI unconditionally, a partial
push failure degrades to "finish the one remaining record by hand," which
is the same manual fallback that already exists for every unconfigured or
failed push today. No new `DnsPushOutcome` value, no rollback of a
successfully-pushed first record.

### 6. Clearer failure message on the actual Azure error

`AzureMtaStsHostProvisioner.EnsureProvisionedAsync`'s first
`containerApp.UpdateAsync(...)` call (the one that adds the new,
not-yet-certificated binding — the point at which Azure actually validates
hostname ownership) gets wrapped:

```csharp
try
{
    containerApp = (await containerApp.UpdateAsync(WaitUntil.Completed, containerApp.Data, cancellationToken).ConfigureAwait(false)).Value;
}
catch (RequestFailedException ex) when (string.Equals(ex.ErrorCode, "InvalidCustomHostNameValidation", StringComparison.Ordinal))
{
    var verificationId = containerApp.Data.CustomDomainVerificationId;
    throw new InvalidOperationException(
        $"Missing ownership verification record — add asuid.{hostname} TXT {verificationId} to DNS; this retries automatically.");
}
```

This exception propagates unchanged through `PollingService.RunSingleMtaStsCheckAsync`'s
existing catch block (`domain.MtaStsCheckDetail = $"Provisioning failed: {ex.Message}"`),
so the clearer message reaches `MtaStsCheckDetail` — and therefore the
domain detail / MTA-STS status UI — with no changes needed to
`PollingService` itself. Any other `RequestFailedException` (or any other
exception) is unaffected and keeps surfacing its own `.Message` exactly as
today.

## Testing

None of the four classes this design touches most
(`AzureMtaStsHostProvisioner`, `CaddyMtaStsHostProvisioner`,
`CloudflareDnsPushProvider`, `AzureDnsPushProvider`) have any existing unit
test coverage — they wrap live SDK clients (`ArmClient` with
`DefaultAzureCredential`, raw Cloudflare `HttpClient` calls, MSAL) with no
fake/mock seam anywhere in the codebase today. Introducing one is out of
scope for this fix; follow the codebase's existing convention for these
classes rather than adding new mocking infrastructure:

- `CaddyMtaStsHostProvisioner.GetDomainVerificationIdAsync`: this one IS
  cheap to test with zero mocking (a plain `null`-returning method on a
  parameterless class) — add a test asserting it returns `null`.
- `AzureMtaStsHostProvisioner`'s new verification-ID fetch and error-message
  wrapping, and both `IDnsPushProvider` implementations' new multi-change
  loop: no unit tests, matching how `EnsureProvisionedAsync`,
  `TeardownAsync`, and both providers' existing `ExchangeAndPushAsync`
  bodies are untested today. Verify manually against a live deployment
  (the same kind of live-Azure check already used earlier in this project
  to confirm the Key Vault write path) before considering this done:
  trigger a fresh domain enable on an Azure-hosted instance without the
  `asuid` record present, confirm the `Failed` detail now names the record
  and value instead of showing raw Azure JSON, add the record, confirm it
  clears on the next poll cycle.
- `ManageMtaSts.razor`: verify manually (per this project's existing
  pattern of browser-testing Blazor pages) that the HelpAlert shows both
  records when `Provisioner` is `"Azure"`, and only the CNAME on a
  Caddy-configured deployment.
- Existing `DnsPushProviderLookupTests` and any other call site
  constructing a `DnsRecordChange` for `ExchangeAndPushAsync` need updating
  to the new `IReadOnlyList<DnsRecordChange>` signature — wrap
  single-change call sites in a one-item list; no behavior change for
  `dmarc`/`tlsrpt`.
