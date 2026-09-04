# MTA-STS Azure Domain Verification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Surface Azure Container Apps' required `asuid.mta-sts.<domain>` ownership-verification TXT record proactively (UI + docs), push it automatically alongside the CNAME when a domain's DNS push happens through Cloudflare or Azure DNS, and replace the raw Azure ARM error with a clear, actionable message when that record is missing.

**Architecture:** `IMtaStsHostProvisioner` gains a method to fetch the Container App's fixed `customDomainVerificationId`; `ManageMtaSts.razor` and two docs pages surface it unconditionally. `IDnsPushProvider.ExchangeAndPushAsync` moves from a single `DnsRecordChange` to a list, so one push action can create both the CNAME and the TXT record under one OAuth exchange. `AzureMtaStsHostProvisioner` catches Azure's specific `InvalidCustomHostNameValidation` error and rethrows with the exact record needed.

**Tech Stack:** .NET 10, Blazor Server, EF Core/Npgsql, Azure.ResourceManager.AppContainers SDK, MudBlazor, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-04-mta-sts-azure-domain-verification-design.md`

## Global Constraints

- No new `MtaStsStatus` state and no change to existing state-machine transitions.
- No caching layer for the verification ID — fetch it fresh on the infrequent paths that need it (page load, DNS push callback), never in `PollingService`'s poll loop.
- No new `DnsPushOutcome` value — a partial multi-record push failure (one record lands, the other doesn't) returns the same failure outcome the failing record would have returned alone; no rollback of an already-pushed record.
- Do not add unit test scaffolding (mocks/fakes for `ArmClient`, Cloudflare's `HttpClient`, or MSAL) for `AzureMtaStsHostProvisioner`, `CloudflareDnsPushProvider`, or `AzureDnsPushProvider` — none of the three have any today, and introducing a testing seam for them is out of scope for this fix. `CaddyMtaStsHostProvisioner`'s new method IS trivially testable with zero mocking and gets a test.
- `IDnsPushProvider.ExchangeAndPushAsync`'s new parameter type is exactly `IReadOnlyList<DnsRecordChange> changes` (not `List<DnsRecordChange>`, not `params`).

---

### Task 1: Fetch the Container App's verification ID, and give the real Azure binding failure a clear message

**Files:**
- Modify: `src/DotMarc/MtaSts/IMtaStsHostProvisioner.cs`
- Modify: `src/DotMarc/MtaSts/CaddyMtaStsHostProvisioner.cs`
- Modify: `src/DotMarc/MtaSts/AzureMtaStsHostProvisioner.cs`
- Test: `test/DotMarc.Tests/MtaSts/CaddyMtaStsHostProvisionerTests.cs` (new)

**Interfaces:**
- Produces: `IMtaStsHostProvisioner.GetDomainVerificationIdAsync(CancellationToken cancellationToken)` returning `Task<string?>` — `null` on Caddy, the Container App's `CustomDomainVerificationId` on Azure. Tasks 2 and 5 both consume this.

- [ ] **Step 1: Add the method to the interface**

Open `src/DotMarc/MtaSts/IMtaStsHostProvisioner.cs`. Add a new method to the interface, and extend the doc comment to mention it:

```csharp
namespace DotMarc.MtaSts;

/// <summary>Provisions (or tears down) whatever the deployment target needs so that
/// mta-sts.&lt;domain&gt; actually serves over valid TLS, once DNS has been verified. See
/// CaddyMtaStsHostProvisioner (self-hosted, no-op — Caddy's own on-demand TLS does the work
/// implicitly) and AzureMtaStsHostProvisioner (Container Apps custom domain + managed
/// certificate).</summary>
public interface IMtaStsHostProvisioner
{
    Task EnsureProvisionedAsync(string domainName, CancellationToken cancellationToken);
    Task TeardownAsync(string domainName, CancellationToken cancellationToken);

    /// <summary>The value Azure Container Apps needs at asuid.&lt;custom-domain&gt; TXT before it
    /// will bind that custom domain — a property of the Container App resource itself, so it is
    /// the same value for every domain this deployment hosts. Null on providers (Caddy) that have
    /// no such concept.</summary>
    Task<string?> GetDomainVerificationIdAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Implement the no-op on Caddy**

Open `src/DotMarc/MtaSts/CaddyMtaStsHostProvisioner.cs`. Add the new method next to the existing two:

```csharp
namespace DotMarc.MtaSts;

/// <summary>Self-hosted deployments: nothing for the app to actively push. Caddy's on-demand TLS
/// (configured in the bundled Caddyfile) requests a certificate implicitly the first time a
/// request for mta-sts.&lt;domain&gt; succeeds through its "ask" callback
/// (GET /.well-known/mta-sts-ask) — which only returns success once DNS has already been verified
/// (see PollingService's MTA-STS cycle), so there's no separate provisioning step to trigger here.
/// Teardown is equally implicit: once MtaStsEnabled is false, "ask" starts 404ing and Caddy simply
/// stops renewing that certificate.</summary>
public sealed class CaddyMtaStsHostProvisioner : IMtaStsHostProvisioner
{
    public Task EnsureProvisionedAsync(string domainName, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task TeardownAsync(string domainName, CancellationToken cancellationToken) => Task.CompletedTask;

    // Caddy's on-demand TLS never does DNS-based domain-ownership verification — there is no
    // per-deployment ID to surface here.
    public Task<string?> GetDomainVerificationIdAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
```

- [ ] **Step 3: Write the Caddy test**

Create `test/DotMarc.Tests/MtaSts/CaddyMtaStsHostProvisionerTests.cs`:

```csharp
using DotMarc.MtaSts;
using Xunit;

namespace DotMarc.Tests.MtaSts;

public sealed class CaddyMtaStsHostProvisionerTests
{
    [Fact]
    public async Task GetDomainVerificationIdAsync_ReturnsNull()
    {
        var provisioner = new CaddyMtaStsHostProvisioner();

        var result = await provisioner.GetDomainVerificationIdAsync(CancellationToken.None);

        Assert.Null(result);
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "FullyQualifiedName~CaddyMtaStsHostProvisionerTests"`
Expected: PASS (this is pure addition, nothing to fail against first — no red/green cycle needed for a one-line no-op method).

- [ ] **Step 5: Implement the verification-ID fetch on Azure, and wrap the failing bind call**

Open `src/DotMarc/MtaSts/AzureMtaStsHostProvisioner.cs`. Add `using Azure;` if not already present (it is, at line 1). Change the `EnsureProvisionedAsync` method's binding step (the block that adds the new `ContainerAppCustomDomain` and calls `UpdateAsync`) to catch the specific validation failure, and add the new interface method. Replace the whole `EnsureProvisionedAsync` method body:

```csharp
    public async Task EnsureProvisionedAsync(string domainName, CancellationToken cancellationToken)
    {
        var hostname = $"mta-sts.{domainName}";
        var containerApp = await GetContainerAppAsync(cancellationToken).ConfigureAwait(false);

        var existingBinding = containerApp.Data.Configuration.Ingress.CustomDomains
            .FirstOrDefault(d => string.Equals(d.Name, hostname, StringComparison.OrdinalIgnoreCase));
        if (existingBinding is not null && existingBinding.BindingType == ContainerAppCustomDomainBindingType.SniEnabled)
        {
            // Already fully bound from an earlier cycle — nothing further to do here. Whether the
            // certificate has actually finished issuing is what the serving self-check
            // (IMtaStsServingVerifier) determines, not this provisioner.
            return;
        }

        if (existingBinding is null)
        {
            // Azure requires the hostname already registered as a custom domain on the container
            // app before it will create a managed certificate for it
            // (RequireCustomHostnameInEnvironment) — so this binds it first with no certificate,
            // then creates the certificate below, then rebinds with the certificate attached. A
            // crash between these two steps leaves the binding Disabled with no certificate; the
            // existingBinding check above only short-circuits once it's fully SniEnabled, so the
            // next cycle resumes from certificate creation instead of re-adding the binding or
            // giving up on it.
            containerApp.Data.Configuration.Ingress.CustomDomains.Add(new ContainerAppCustomDomain(hostname) { BindingType = ContainerAppCustomDomainBindingType.Disabled });
            try
            {
                containerApp = (await containerApp.UpdateAsync(WaitUntil.Completed, containerApp.Data, cancellationToken).ConfigureAwait(false)).Value;
            }
            catch (RequestFailedException ex) when (string.Equals(ex.ErrorCode, "InvalidCustomHostNameValidation", StringComparison.Ordinal))
            {
                // Azure validates hostname ownership at exactly this call — this is the ARM error
                // the user hits when the asuid.<hostname> TXT record isn't in place yet. Replace
                // the raw error with the specific fix, using the same verification ID
                // GetDomainVerificationIdAsync below would return (already loaded on this same
                // containerApp.Data, so no extra ARM call needed).
                var verificationId = containerApp.Data.CustomDomainVerificationId;
                throw new InvalidOperationException(
                    $"Missing ownership verification record — add asuid.{hostname} TXT {verificationId} to DNS; this retries automatically.");
            }
        }

        var certificateId = await EnsureManagedCertificateAsync(hostname, cancellationToken).ConfigureAwait(false);

        var binding = containerApp.Data.Configuration.Ingress.CustomDomains
            .First(d => string.Equals(d.Name, hostname, StringComparison.OrdinalIgnoreCase));
        binding.CertificateId = certificateId;
        binding.BindingType = ContainerAppCustomDomainBindingType.SniEnabled;
        await containerApp.UpdateAsync(WaitUntil.Completed, containerApp.Data, cancellationToken).ConfigureAwait(false);
    }
```

Then add the new interface method as a new public method anywhere in the class (after `TeardownAsync` is a natural spot):

```csharp
    /// <summary>The Container App's own customDomainVerificationId — fixed for the life of the
    /// resource, so the same value is correct for every domain this deployment hosts. Not cached:
    /// this is only called from a page load or a DNS push callback, both human-triggered and
    /// infrequent, never from PollingService's poll loop.</summary>
    public async Task<string?> GetDomainVerificationIdAsync(CancellationToken cancellationToken)
    {
        var containerApp = await GetContainerAppAsync(cancellationToken).ConfigureAwait(false);
        return containerApp.Data.CustomDomainVerificationId;
    }
```

- [ ] **Step 6: Build to confirm it compiles**

Run: `dotnet build src/DotMarc/DotMarc.csproj`
Expected: Build succeeds, no new warnings from this file.

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/MtaSts/IMtaStsHostProvisioner.cs src/DotMarc/MtaSts/CaddyMtaStsHostProvisioner.cs src/DotMarc/MtaSts/AzureMtaStsHostProvisioner.cs test/DotMarc.Tests/MtaSts/CaddyMtaStsHostProvisionerTests.cs
git commit -m "Surface Azure's asuid verification ID and give its binding failure a clear message"
```

---

### Task 2: Show the required asuid TXT record on the Manage MTA-STS page

**Files:**
- Modify: `src/DotMarc/Components/Pages/ManageMtaSts.razor`

**Interfaces:**
- Consumes: `IMtaStsHostProvisioner.GetDomainVerificationIdAsync(CancellationToken)` from Task 1.

- [ ] **Step 1: Inject the provisioner and fetch the verification ID once on load**

Open `src/DotMarc/Components/Pages/ManageMtaSts.razor`. Add the injection alongside the existing ones near the top of the file:

```razor
@inject IMtaStsHostProvisioner MtaStsHostProvisioner
```

(add it directly after the existing `@inject IDnsProviderDetector DnsProviderDetector` line).

In the `@code` block, add a field and populate it in `OnInitializedAsync`, right after the existing `await LoadAsync();` call:

```csharp
    private string? _domainVerificationId;

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();

        if (string.Equals(MtaStsOptions.Value.Provisioner, "Azure", StringComparison.OrdinalIgnoreCase))
        {
            _domainVerificationId = await MtaStsHostProvisioner.GetDomainVerificationIdAsync(CancellationToken.None);
        }

        if (RendererInfo.IsInteractive)
        {
            ShowDnsPushResultToast();
        }
    }
```

- [ ] **Step 2: Show the second record in the HelpAlert**

Find the existing Azure-only branch inside the `HelpAlert` block (the `@if (string.Equals(MtaStsOptions.Value.Provisioner, "Azure", ...))` block that explains the raw Container App hostname). Extend its text to also list the TXT record, only once the ID has actually loaded:

```razor
        @if (string.Equals(MtaStsOptions.Value.Provisioner, "Azure", StringComparison.OrdinalIgnoreCase))
        {
            <text> That's this deployment's raw Container App hostname, not a nicer custom domain,
            because Azure's free managed certificates refuse to issue through an intermediate CNAME
            like one. Each enabled domain adds its own custom domain and certificate to this app;
            Azure doesn't publish a hard limit on either, so there's no cap to plan around here.
            @if (!string.IsNullOrEmpty(_domainVerificationId))
            {
                <text> Azure also requires a second record proving you control the domain, the same
                value for every domain on this deployment:
                <code>asuid.mta-sts.&lt;domain&gt; TXT @_domainVerificationId</code>.</text>
            }</text>
        }
```

This replaces only the inner `@if` block's contents — the surrounding structure (the outer `@if`/`<text>` for the Azure branch) stays as-is.

- [ ] **Step 3: Build and manually verify**

Run: `dotnet build src/DotMarc/DotMarc.csproj`
Expected: Build succeeds.

There is no component test harness in this project for Razor pages (checked: no `test/DotMarc.Tests/Components/` directory exists) — this step is manually verified instead, either now or as part of Task 1's manual Azure verification note in the spec. If you have a local `MtaSts__Provisioner=Azure` deployment or the demo/dev environment reachable, load `/mta-sts` and confirm the second record line renders with a real-looking GUID-shaped value; on a `Provisioner=Caddy` deployment (the Docker Compose default), confirm the HelpAlert shows only the CNAME line as before. If neither environment is reachable in this task, note that in your report as a DONE_WITH_CONCERNS — final review will re-check the rendered markup by reading the diff.

- [ ] **Step 4: Commit**

```bash
git add src/DotMarc/Components/Pages/ManageMtaSts.razor
git commit -m "Show the required asuid verification TXT record on Manage MTA-STS"
```

---

### Task 3: Document the second DNS record

**Files:**
- Modify: `website/docs/getting-started.mdx`
- Modify: `website/docs/mta-sts.mdx`

- [ ] **Step 1: Update getting-started.mdx's MTA-STS section**

Open `website/docs/getting-started.mdx`. Find step 3 in the "MTA-STS policy hosting (optional)" section (the one showing the CNAME customers add). Add a new step 4 after it (renumbering isn't needed — Docusaurus/MDX numbered lists don't require sequential literal numbers, but write it as `4.` for readability), just before the "**If something is already bound to port 443/80...**" paragraph:

```markdown
3. For each domain a customer wants hosted, they add one CNAME:

   ```txt
   mta-sts.<their-domain> CNAME <your MtaSts__HostingHostname value>
   ```

4. **Running on Azure only:** Container Apps also requires a domain-ownership TXT record before
   it will bind the custom domain, in addition to the CNAME above:

   ```txt
   asuid.mta-sts.<their-domain> TXT <this deployment's verification ID>
   ```

   The value is the same for every domain this deployment hosts — find it on the **Manage
   MTA-STS** page once any domain is enabled, or look it up directly:

   ```powershell
   az containerapp show --name <container-app-name> --resource-group <resource-group> --query "properties.customDomainVerificationId" -o tsv
   ```

   Self-hosted (Caddy) deployments don't need this — Caddy's on-demand TLS never checks domain
   ownership via DNS.
```

- [ ] **Step 2: Update mta-sts.mdx's "Enabling a domain" section**

Open `website/docs/mta-sts.mdx`. Find the "Enabling a domain" section (currently reads "That CNAME is the only DNS change needed."). Replace that sentence — it's inaccurate for Azure-hosted instances — with:

```markdown
## Enabling a domain

From **Manage MTA-STS**, toggle a domain on and add a CNAME for it:

```txt
mta-sts.<domain> CNAME <the hosting hostname shown on the Manage MTA-STS page>
```

dotMARC verifies it, provisions a certificate, and starts serving the policy automatically once
it resolves. If this deployment runs on Azure, one more record is needed before Azure will bind
the custom domain — the Manage MTA-STS page shows its exact value once you're viewing it, right
below the CNAME instructions. Self-hosted deployments need only the CNAME above.
```

- [ ] **Step 3: Commit**

```bash
git add website/docs/getting-started.mdx website/docs/mta-sts.mdx
git commit -m "Document the Azure-only asuid verification TXT record"
```

---

### Task 4: Extend DNS push to carry multiple record changes in one exchange

**Files:**
- Modify: `src/DotMarc/DnsPush/IDnsPushProvider.cs`
- Modify: `src/DotMarc/DnsPush/CloudflareDnsPushProvider.cs`
- Modify: `src/DotMarc/DnsPush/AzureDnsPushProvider.cs`
- Modify: `test/DotMarc.Tests/DnsPush/DnsPushProviderLookupTests.cs`

**Interfaces:**
- Produces: `IDnsPushProvider.ExchangeAndPushAsync(string code, string codeVerifier, string redirectUri, IReadOnlyList<DnsRecordChange> changes, CancellationToken cancellationToken)` — signature change from a single `DnsRecordChange change`. Task 5 consumes this.
- Behavior: changes are pushed in list order against one token exchange; the first non-`Pushed` result short-circuits and is returned as-is; an empty or all-succeeding list returns `DnsPushResult(DnsPushOutcome.Pushed, null)`.

- [ ] **Step 1: Change the interface signature**

Open `src/DotMarc/DnsPush/IDnsPushProvider.cs`. Change the `ExchangeAndPushAsync` signature and its doc comment:

```csharp
namespace DotMarc.DnsPush;

public enum DnsPushOutcome { Pushed, ZoneNotFound, ProviderError }

public sealed record DnsPushResult(DnsPushOutcome Outcome, string? DetailMessage);

/// <summary>One implementation per supported DNS provider. The provider's own OAuth client
/// credentials are DB-backed (CloudflareDnsSettings/AzureDnsSettings), read fresh per call rather
/// than cached — IsConfiguredAsync/BuildAuthorizationUrlAsync need a DB round trip, which is why
/// both are async even though they're conceptually simple lookups. Everything about the end-user's
/// own OAuth exchange stays exactly as stateless as before: nothing about the access token is ever
/// persisted; it exists only as a local variable for the duration of ExchangeAndPushAsync.</summary>
public interface IDnsPushProvider
{
    /// <summary>Matches DetectedDnsProvider and the {provider} route segment in
    /// /dns-push/{provider}/start|callback — "cloudflare" or "azure-dns".</summary>
    string ProviderKey { get; }

    /// <summary>False when this provider's OAuth app isn't configured for this deployment — a
    /// push attempt against this provider then fails with a "no configured option" message rather
    /// than being attempted.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default);

    Task<string> BuildAuthorizationUrlAsync(string state, string codeChallenge, string redirectUri, CancellationToken cancellationToken = default);

    /// <summary>Pushes every change in order against one token exchange (the authorization code is
    /// single-use, so all changes for one push action have to ride the same exchange). Stops at
    /// the first change that doesn't return Pushed and returns that result — a change already
    /// pushed before a later one fails is NOT rolled back.</summary>
    Task<DnsPushResult> ExchangeAndPushAsync(
        string code, string codeVerifier, string redirectUri, IReadOnlyList<DnsRecordChange> changes, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Update CloudflareDnsPushProvider to loop over changes**

Open `src/DotMarc/DnsPush/CloudflareDnsPushProvider.cs`. Replace the `ExchangeAndPushAsync` method:

```csharp
    public async Task<DnsPushResult> ExchangeAndPushAsync(
        string code, string codeVerifier, string redirectUri, IReadOnlyList<DnsRecordChange> changes, CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var clientSecret = await _secretStore.GetSecretAsync(CloudflareDnsSettings.SecretStoreKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(settings.ClientId) || string.IsNullOrEmpty(clientSecret))
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, "Cloudflare push is not configured for this deployment.");
        }

        var accessToken = await ExchangeCodeForTokenAsync(settings.ClientId, clientSecret, code, codeVerifier, redirectUri, cancellationToken).ConfigureAwait(false);
        if (accessToken is null)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, "Cloudflare rejected the authorization code exchange.");
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
        var zoneName = ZoneNameFor(change.Name);
        var (zoneId, zoneErrorStatus) = await FindZoneIdAsync(zoneName, accessToken, cancellationToken).ConfigureAwait(false);
        if (zoneErrorStatus.HasValue)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Cloudflare rejected the zone lookup ({zoneErrorStatus}).");
        }
        if (zoneId is null)
        {
            return new DnsPushResult(DnsPushOutcome.ZoneNotFound, $"Couldn't find {zoneName} in the Cloudflare account you authorized.");
        }

        return change.Kind == DnsRecordChangeKind.Merge
            ? await UpdateExistingRecordAsync(zoneId, accessToken, change, cancellationToken).ConfigureAwait(false)
            : await CreateRecordAsync(zoneId, accessToken, change, cancellationToken).ConfigureAwait(false);
    }
```

This replaces the old body of `ExchangeAndPushAsync`, which did the token exchange and then inlined what is now `PushOneChangeAsync`'s body for a single `change`. `CreateRecordAsync`, `UpdateExistingRecordAsync`, `FindZoneIdAsync`, `ExchangeCodeForTokenAsync`, `GetSettingsAsync`, and `ZoneNameFor` are unchanged — only the entry point changed shape.

- [ ] **Step 3: Update AzureDnsPushProvider to loop over changes**

Open `src/DotMarc/DnsPush/AzureDnsPushProvider.cs`. Replace the `ExchangeAndPushAsync` method:

```csharp
    public async Task<DnsPushResult> ExchangeAndPushAsync(
        string code, string codeVerifier, string redirectUri, IReadOnlyList<DnsRecordChange> changes, CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var clientSecret = await _secretStore.GetSecretAsync(AzureDnsSettings.SecretStoreKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(settings.TenantId) || string.IsNullOrEmpty(settings.ClientId) || string.IsNullOrEmpty(clientSecret))
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, "Azure DNS push is not configured for this deployment.");
        }

        var confidentialClient = ConfidentialClientApplicationBuilder.Create(settings.ClientId)
            .WithClientSecret(clientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{settings.TenantId}")
            .WithRedirectUri(redirectUri)
            .Build();

        AuthenticationResult authResult;
        try
        {
            authResult = await confidentialClient
                .AcquireTokenByAuthorizationCode([Scope], code)
                .WithPkceCodeVerifier(codeVerifier)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MsalException ex)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Azure rejected the authorization code exchange: {ex.Message}");
        }

        var armClient = new ArmClient(new FixedTokenCredential(authResult.AccessToken, authResult.ExpiresOn));

        foreach (var change in changes)
        {
            var result = await PushOneChangeAsync(armClient, change, cancellationToken).ConfigureAwait(false);
            if (result.Outcome != DnsPushOutcome.Pushed)
            {
                return result;
            }
        }

        return new DnsPushResult(DnsPushOutcome.Pushed, null);
    }

    private static async Task<DnsPushResult> PushOneChangeAsync(ArmClient armClient, DnsRecordChange change, CancellationToken cancellationToken)
    {
        var zoneName = ZoneNameFor(change.Name);
        var zone = await FindZoneAsync(armClient, zoneName, cancellationToken).ConfigureAwait(false);
        if (zone is null)
        {
            return new DnsPushResult(DnsPushOutcome.ZoneNotFound,
                $"Couldn't find {zoneName} in any subscription you authorized — check you have DNS Zone Contributor rights on it.");
        }

        return await PushRecordAsync(zone, zoneName, change, cancellationToken).ConfigureAwait(false);
    }
```

This replaces the old body of `ExchangeAndPushAsync`, which did the MSAL exchange and then inlined what is now `PushOneChangeAsync`'s body for a single `change`. `FindZoneAsync`, `PushRecordAsync`, `ZoneNameFor`, `GetSettingsAsync`, and `FixedTokenCredential` are unchanged — only the entry point changed shape.

- [ ] **Step 4: Update the test double's signature**

Open `test/DotMarc.Tests/DnsPush/DnsPushProviderLookupTests.cs`. Change `FakeDnsPushProvider.ExchangeAndPushAsync`'s parameter from `DnsRecordChange change` to `IReadOnlyList<DnsRecordChange> changes` — it already throws `NotImplementedException` unconditionally, so this is a signature-only change:

```csharp
        public Task<DnsPushResult> ExchangeAndPushAsync(string code, string codeVerifier, string redirectUri, IReadOnlyList<DnsRecordChange> changes, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
```

- [ ] **Step 5: Build and run existing tests**

Run: `dotnet build src/DotMarc/DotMarc.csproj && dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "FullyQualifiedName~DnsPush"`
Expected: Build succeeds; all existing `DnsPush` tests pass unchanged (none of them exercise `ExchangeAndPushAsync` directly today, per the spec's testing section — this step confirms the signature change didn't break compilation or any test that does touch the interface, like `DnsPushProviderLookupTests`).

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/DnsPush/IDnsPushProvider.cs src/DotMarc/DnsPush/CloudflareDnsPushProvider.cs src/DotMarc/DnsPush/AzureDnsPushProvider.cs test/DotMarc.Tests/DnsPush/DnsPushProviderLookupTests.cs
git commit -m "Let one DNS push exchange carry multiple record changes"
```

---

### Task 5: Push the asuid TXT record alongside the CNAME for Azure-hosted MTA-STS

**Files:**
- Modify: `src/DotMarc/Program.cs`

**Interfaces:**
- Consumes: `IDnsPushProvider.ExchangeAndPushAsync(..., IReadOnlyList<DnsRecordChange> changes, ...)` from Task 4; `IMtaStsHostProvisioner.GetDomainVerificationIdAsync(CancellationToken)` from Task 1.

- [ ] **Step 1: Inject IMtaStsHostProvisioner into the callback endpoint and build a list of changes**

Open `src/DotMarc/Program.cs`. Find the `/dns-push/{provider}/callback` endpoint (starts at line 462). Add `DotMarc.MtaSts.IMtaStsHostProvisioner mtaStsHostProvisioner` to its parameter list (the lambda already takes several injected services — add it alongside `IOptions<DotMarc.MtaSts.MtaStsOptions> mtaStsOptions`).

Then change the `DnsRecordChange change;` declaration and its three assignment branches to build a `List<DnsRecordChange>` instead. Replace this whole block:

```csharp
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
    else if (decodedState.PushTarget == "dmarc")
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
    else
    {
        var mailbox = graphOptions.Value.TlsrptMailboxAddress;
        if (string.IsNullOrWhiteSpace(mailbox))
        {
            return Results.Redirect($"{returnPath}?dnsPush=error");
        }

        var existing = await tlsrptTxtLookup.LookupAsync(domain.Name, CancellationToken.None);
        if (existing is null)
        {
            change = new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"_smtp._tls.{domain.Name}", $"v=TLSRPTv1; rua=mailto:{mailbox}", null);
        }
        else
        {
            var merged = TlsrptRuaMerge.TryMerge(existing, mailbox);
            if (merged is null)
            {
                return Results.Redirect($"{returnPath}?dnsPush=unmergeable");
            }
            change = new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"_smtp._tls.{domain.Name}", merged, existing);
        }
    }
```

with:

```csharp
    List<DnsRecordChange> changes;
    if (decodedState.PushTarget == "mta-sts")
    {
        var hostingHostname = mtaStsOptions.Value.HostingHostname;
        if (string.IsNullOrEmpty(hostingHostname))
        {
            return Results.Redirect($"{returnPath}?dnsPush=error");
        }
        changes = [new DnsRecordChange(DnsRecordChangeKind.Create, "CNAME", $"mta-sts.{domain.Name}", hostingHostname, null)];

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
                changes.Add(new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"asuid.mta-sts.{domain.Name}", verificationId, null));
            }
        }
    }
    else if (decodedState.PushTarget == "dmarc")
    {
        var existing = await dmarcTxtLookup.LookupAsync(domain.Name, CancellationToken.None);
        var mailbox = graphOptions.Value.MailboxAddress;
        if (existing is null)
        {
            changes = [new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"_dmarc.{domain.Name}", $"v=DMARC1; p=none; rua=mailto:{mailbox}", null)];
        }
        else
        {
            var merged = DmarcRuaMerge.TryMerge(existing, mailbox);
            if (merged is null)
            {
                return Results.Redirect($"{returnPath}?dnsPush=unmergeable");
            }
            changes = [new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"_dmarc.{domain.Name}", merged, existing)];
        }
    }
    else
    {
        var mailbox = graphOptions.Value.TlsrptMailboxAddress;
        if (string.IsNullOrWhiteSpace(mailbox))
        {
            return Results.Redirect($"{returnPath}?dnsPush=error");
        }

        var existing = await tlsrptTxtLookup.LookupAsync(domain.Name, CancellationToken.None);
        if (existing is null)
        {
            changes = [new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"_smtp._tls.{domain.Name}", $"v=TLSRPTv1; rua=mailto:{mailbox}", null)];
        }
        else
        {
            var merged = TlsrptRuaMerge.TryMerge(existing, mailbox);
            if (merged is null)
            {
                return Results.Redirect($"{returnPath}?dnsPush=unmergeable");
            }
            changes = [new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"_smtp._tls.{domain.Name}", merged, existing)];
        }
    }
```

- [ ] **Step 2: Update the ExchangeAndPushAsync call site**

A few lines below, find:

```csharp
    var redirectUri = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/dns-push/{provider}/callback";
    var result = await pushProvider.ExchangeAndPushAsync(code, decodedState.CodeVerifier, redirectUri, [change], CancellationToken.None);
```

(Note: Task 4 already touched this exact line as a compile-fix for its own interface signature change — it wrapped the old `change` variable in a one-element collection expression, `[change]`, to keep the build green before this task's list-building logic existed. That's why the "find" text above shows `[change]` rather than the bare `change` an earlier read of this file might have shown.)

Change `[change]` to `changes`:

```csharp
    var redirectUri = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/dns-push/{provider}/callback";
    var result = await pushProvider.ExchangeAndPushAsync(code, decodedState.CodeVerifier, redirectUri, changes, CancellationToken.None);
```

- [ ] **Step 3: Build**

Run: `dotnet build src/DotMarc/DotMarc.csproj`
Expected: Build succeeds — this endpoint has no dedicated unit tests today (it's a top-level `MapGet` lambda in `Program.cs`, exercised only through the full app), so a clean build plus Task 4's `DnsPush` test pass are the available verification for this task; final review should re-read the diff for correctness of the branch logic.

- [ ] **Step 4: Commit**

```bash
git add src/DotMarc/Program.cs
git commit -m "Push the asuid verification TXT record alongside the MTA-STS CNAME on Azure"
```
