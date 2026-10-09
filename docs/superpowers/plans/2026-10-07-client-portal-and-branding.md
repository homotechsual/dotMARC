# Client Portal and Branding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give client contacts a simplified, read-only, branded portal of their own Groups' domains, under the MSP's brand with optional per-Group overrides.

**Architecture:** A `DotMarc.Portal` namespace holds the portal's pure logic (`PortalStatus`, `PortalTrend`, `PortalBranding`, `BrandColours`, `BrandingImages`) and its data loader (`PortalData`). A global authorization handler (`ClientPortalGate`) fails every policy for users carrying the `dotmarc:client-portal` claim except the `ClientPortal` policy, so internal pages, the API and every write are closed to them; cookie access-denied redirects send them to `/portal`. Portal pages use their own `PortalLayout` with a theme built from the resolved brand. Branding lives in `BrandingSettings` (singleton), `GroupBranding` (per Group) and `BrandingImage` (logo bytes), served from `/branding/logo/{id}`.

**Tech Stack:** .NET 10, Blazor Server, MudBlazor 9.8, EF Core 10 with Npgsql, xUnit 2.9 with the Testcontainers Postgres fixture (`[Collection("Postgres")]`, `PostgresContainerFixture.CreateDatabaseAsync()`), `WebApplicationFactory<Program>` in demo mode.

**Spec:** `docs/superpowers/specs/2026-10-07-client-portal-and-branding-design.md`

## Global Constraints

- The portal is read-only whatever the person's role says; a portal user is denied every internal page, every API endpoint and every write.
- A portal user only ever sees domains in their scoped Groups. A portal user with no scoped Groups sees no domains (never all of them).
- `UserAccess.IsClientPortal` can only be on for a grant with at least one scoped Group on a scopable role.
- Logos: PNG, JPEG or SVG only, at most 512 KB; SVGs with `<script>`, `<foreignObject>`, `on*` attributes, or an `href`/`xlink:href` other than `#...` or `data:image/...` are refused.
- Colours are `#RRGGBB`. Low contrast warns, never refuses.
- Every public static method on a service listed in `test/DotMarc.Tests/Audit/AuditCoverageTests.cs` that changes data takes `(DotMarcDbContext context, AuditActor actor, ...)` first, unless its name starts with Get, List, Count or Resolve.
- No em dashes in user-facing text, docs or comments. Meaningful variable names everywhere, tests included.
- Use playwright-edge for browser checks; stop only processes you started, by process ID.
- The test suite stays green at the end of every task: `dotnet test test/DotMarc.Tests`.

## Review Focus

1. **A portal grant losing its Groups.** Deleting a Group, or a role becoming non-scopable, would leave a portal grant unscoped, which elsewhere means "every domain". Group deletion is refused for a portal grant's last Group (Task 1), and `PortalData` treats a portal user with no scoped-group claims as seeing nothing (Task 4). Both pinned by tests.
2. **A portal user typing an internal URL or calling the API.** Every policy, including `AuthorizeView` policies, the fallback policy and the API policies, must deny them. Pinned by web tests over `/dashboard`, `/domains`, `/groups`, `/alerts/settings`, `/access` and `/api/v1/domains` (Task 2).
3. **A domain outside the client's Groups opened by name.** `/portal/domains/{name}` must show "not found", not the domain. Pinned in Task 4.
4. **A hostile SVG.** An SVG with script, event handlers, `foreignObject` or external references must be refused on upload, and SVGs must be served with a locking CSP and nosniff. Pinned in Tasks 5 and 6.
5. **A client in several Groups with different branding.** The MSP default applies, and the heading is the product name. Pinned in Task 8.

---

## Phase 1: Portal access and pages

### Task 1: The client portal switch on access grants

**Files:**
- Modify: `src/DotMarc/Data/UserAccess.cs`, `src/DotMarc/Data/UserAccessManagementService.cs`, `src/DotMarc/Data/GroupManagementService.cs`, `src/DotMarc/Audit/AuditActions.cs`
- Modify: `src/DotMarc/Security/UserAccessClaimsTransformation.cs`
- Create: migration `AddClientPortalFlag`
- Test: `test/DotMarc.Tests/Data/UserAccessManagementServiceTests.cs`, `test/DotMarc.Tests/Data/GroupManagementServiceTests.cs`, `test/DotMarc.Tests/Security/UserAccessClaimsTransformationTests.cs` (or the existing claims transformation test file; search for `UserAccessClaimsTransformation` under `test/`)

**Interfaces:**
- Produces:
  - `UserAccess.IsClientPortal` (bool)
  - `UserAccessClaimsTransformation.ClientPortalClaimType = "dotmarc:client-portal"` (value `"true"`)
  - `UserAccessManagementService.SetClientPortalAsync(DotMarcDbContext context, AuditActor actor, int userAccessId, bool isClientPortal, CancellationToken ct = default) : Task<SetClientPortalResult>`, `enum SetClientPortalResult { Updated, NeedsScopedGroups }`
  - `UpdateAccessResult.ClientPortalNeedsGroups` (new member)
  - `GroupManagementService.RemoveGroupResult.LastGroupOfClientPortal` (new member)
  - `AuditActions.AccessClientPortalChanged = "access.client_portal_changed"` ("Client portal access changed")

- [ ] **Step 1: Write the failing tests**

In `UserAccessManagementServiceTests.cs` (use the file's existing scaffolding, role helpers and `TestActors.Admin`):

```csharp
[Fact]
public async Task SetClientPortalAsync_OnAScopedGrant_TurnsItOnAndAudits()
{
    await using var context = CreateContext();
    var viewerRole = new Role { Name = "Client viewer", IsScopable = true, Permissions = [Permission.DomainsView] };
    var group = new Group { Name = "Aurora Retail" };
    var access = new UserAccess { Email = "client@aurora.example", Role = viewerRole, ScopedGroups = [group] };
    context.UserAccesses.Add(access);
    await context.SaveChangesAsync();

    var result = await UserAccessManagementService.SetClientPortalAsync(context, TestActors.Admin, access.Id, true);

    Assert.Equal(UserAccessManagementService.SetClientPortalResult.Updated, result);
    await using var verify = CreateContext();
    Assert.True((await verify.UserAccesses.SingleAsync()).IsClientPortal);
    var entry = await verify.AuditEntries.SingleAsync();
    Assert.Equal(AuditActions.AccessClientPortalChanged, entry.Action);
    Assert.Contains(entry.Changes, change => change.Field == "Client portal" && change.New == "Yes");
}

[Fact]
public async Task SetClientPortalAsync_OnAnUnscopedGrant_IsRefused()
{
    await using var context = CreateContext();
    var viewerRole = new Role { Name = "Client viewer", IsScopable = true, Permissions = [Permission.DomainsView] };
    var access = new UserAccess { Email = "client@aurora.example", Role = viewerRole };
    context.UserAccesses.Add(access);
    await context.SaveChangesAsync();

    var result = await UserAccessManagementService.SetClientPortalAsync(context, TestActors.Admin, access.Id, true);

    Assert.Equal(UserAccessManagementService.SetClientPortalResult.NeedsScopedGroups, result);
    await using var verify = CreateContext();
    Assert.False((await verify.UserAccesses.SingleAsync()).IsClientPortal);
    Assert.Empty(verify.AuditEntries);
}

[Fact]
public async Task UpdateAccessAsync_RemovingAPortalGrantsLastGroup_IsRefused()
{
    await using var context = CreateContext();
    var viewerRole = new Role { Name = "Client viewer", IsScopable = true, Permissions = [Permission.DomainsView] };
    var access = new UserAccess { Email = "client@aurora.example", Role = viewerRole, ScopedGroups = [new Group { Name = "Aurora Retail" }], IsClientPortal = true };
    context.UserAccesses.Add(access);
    await context.SaveChangesAsync();

    var result = await UserAccessManagementService.UpdateAccessAsync(context, TestActors.Admin, access.Id, viewerRole.Id, []);

    Assert.Equal(UserAccessManagementService.UpdateAccessResult.ClientPortalNeedsGroups, result);
}
```

(Check `Permission` for the view permission's actual name, such as `DomainsView`, and use the one the Viewer role has in `AccessBootstrapper.ViewerPermissions`.)

In `GroupManagementServiceTests.cs`:

```csharp
[Fact]
public async Task RemoveGroupAsync_APortalGrantsLastGroup_IsRefused()
{
    await using var context = CreateContext();
    var viewerRole = new Role { Name = "Client viewer", IsScopable = true, Permissions = [] };
    var group = new Group { Name = "Aurora Retail" };
    context.UserAccesses.Add(new UserAccess { Email = "client@aurora.example", Role = viewerRole, ScopedGroups = [group], IsClientPortal = true });
    await context.SaveChangesAsync();

    var result = await GroupManagementService.RemoveGroupAsync(context, TestActors.Admin, group.Id);

    Assert.Equal(GroupManagementService.RemoveGroupResult.LastGroupOfClientPortal, result);
    await using var verify = CreateContext();
    Assert.Single(verify.Groups);
}

[Fact]
public async Task RemoveGroupAsync_APortalGrantWithOtherGroups_IsAllowed()
{
    await using var context = CreateContext();
    var viewerRole = new Role { Name = "Client viewer", IsScopable = true, Permissions = [] };
    var aurora = new Group { Name = "Aurora Retail" };
    var auroraOnline = new Group { Name = "Aurora Online" };
    context.UserAccesses.Add(new UserAccess { Email = "client@aurora.example", Role = viewerRole, ScopedGroups = [aurora, auroraOnline], IsClientPortal = true });
    await context.SaveChangesAsync();

    Assert.Equal(GroupManagementService.RemoveGroupResult.Removed, await GroupManagementService.RemoveGroupAsync(context, TestActors.Admin, aurora.Id));
}
```

In the claims transformation tests, add a case that a grant with `IsClientPortal = true` gets `ClientPortalClaimType` = `"true"`, and one without it gets no such claim, following the file's existing test for scoped-group claims.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~UserAccessManagementServiceTests|FullyQualifiedName~GroupManagementServiceTests|FullyQualifiedName~UserAccessClaimsTransformation"`
Expected: build FAIL (`IsClientPortal`, `SetClientPortalAsync` missing).

- [ ] **Step 3: Implement**

`UserAccess`: add `public bool IsClientPortal { get; set; }` with the summary "Sees the client portal (a simplified, branded, read-only view of the scoped Groups' domains) instead of the app. Only valid with at least one scoped Group."

`UserAccessManagementService`:

```csharp
public enum SetClientPortalResult { Updated, NeedsScopedGroups }

/// <summary>Turns the client portal on or off for a grant. A portal grant must be limited to Groups: with none it would
/// see every domain, which is never what a client should get.</summary>
public static async Task<SetClientPortalResult> SetClientPortalAsync(DotMarcDbContext context, AuditActor actor, int userAccessId, bool isClientPortal, CancellationToken cancellationToken = default)
{
    var access = await context.UserAccesses.Include(u => u.Role).Include(u => u.ScopedGroups).AsSplitQuery()
        .SingleAsync(u => u.Id == userAccessId, cancellationToken).ConfigureAwait(false);
    if (isClientPortal && (!access.Role.IsScopable || access.ScopedGroups.Count == 0))
    {
        return SetClientPortalResult.NeedsScopedGroups;
    }

    var changes = new AuditChanges().Field("Client portal", access.IsClientPortal, isClientPortal);
    if (!changes.Any)
    {
        return SetClientPortalResult.Updated;
    }

    access.IsClientPortal = isClientPortal;
    AuditLog.Record(context, actor, AuditActions.AccessClientPortalChanged, AuditTarget.For(access), $"{(isClientPortal ? "Turned on" : "Turned off")} the client portal for {access.Email}", changes);
    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    return SetClientPortalResult.Updated;
}
```

`UpdateAccessResult` gains `ClientPortalNeedsGroups`; in `UpdateAccessAsync`, after computing `groups`, add:

```csharp
if (access.IsClientPortal && groups.Count == 0)
{
    return UpdateAccessResult.ClientPortalNeedsGroups;
}
```

`GroupManagementService.RemoveGroupResult` gains `LastGroupOfClientPortal`; in `RemoveGroupAsync`, after the API key check:

```csharp
// A portal grant left with no Groups would see every domain, so its last Group can't be removed.
var lastGroupOfAPortalGrant = await context.UserAccesses
    .AnyAsync(access => access.IsClientPortal && access.ScopedGroups.Count == 1 && access.ScopedGroups.Any(scopedGroup => scopedGroup.Id == groupId), cancellationToken)
    .ConfigureAwait(false);
if (lastGroupOfAPortalGrant)
{
    return RemoveGroupResult.LastGroupOfClientPortal;
}
```

`ManageGroups.razor`'s delete handler: on `LastGroupOfClientPortal`, show "Can't remove {name}. It's the only Group a client portal user can see. Change their access on the Access page first."

`UserAccessClaimsTransformation`: add `public const string ClientPortalClaimType = "dotmarc:client-portal";` and, after the access claims, `if (access.IsClientPortal) identity.AddClaim(new Claim(ClientPortalClaimType, "true"));`.

Audit action and label. Migration: `dotnet ef migrations add AddClientPortalFlag --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj` (one bool column, default false).

- [ ] **Step 4: Run the tests, then the suite, then commit**

Run the Step 2 filter (PASS), then `dotnet test test/DotMarc.Tests` (all pass).

```bash
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Add the client portal switch to access grants"
```

---

### Task 2: Closing internal pages to portal users, and the demo Client persona

**Files:**
- Create: `src/DotMarc/Portal/ClientPortalGate.cs`
- Modify: `src/DotMarc/Program.cs` (policy, handler, cookie redirects, demo persona), `src/DotMarc/Demo/DemoDataSeeder.cs`, `src/DotMarc/Components/Pages/Demo/*` (persona button; find the demo sign-in page under `Components/Pages/Demo`)
- Create: `src/DotMarc/Components/Pages/Portal/PortalHome.razor` (placeholder heading only; Task 4 fills it)
- Test: `test/DotMarc.Tests/Portal/ClientPortalAccessTests.cs`

**Interfaces:**
- Consumes: `UserAccessClaimsTransformation.ClientPortalClaimType`, `UserAccess.IsClientPortal`.
- Produces:
  - `ClientPortalRequirement : IAuthorizationRequirement` and `ClientPortalGate : IAuthorizationHandler` (singleton)
  - policy `"ClientPortal"`
  - `ClientPortalRedirects.OnRedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions>) : Task`
  - `DemoDataSeeder.ClientEmail = "demo-client@aurora-retail.example"`, demo persona `client`
  - route `/portal` (page `PortalHome`, `[Authorize(Policy = "ClientPortal")]`, `@layout PortalLayout` from Task 4; in this task use the default layout)

- [ ] **Step 1: Write the failing web tests**

```csharp
// test/DotMarc.Tests/Portal/ClientPortalAccessTests.cs
using System.Net;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DotMarc.Tests.Portal;

[Collection("Postgres")]
public sealed class ClientPortalAccessTests : IAsyncLifetime
{
    // Scaffolding as DemoSignInEndpointTests: a database, a factory with ConnectionStrings:DotMarc and Demo:Enabled=true.

    private async Task<HttpClient> SignInAsync(string persona)
    {
        var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync($"/demo/sign-in/{persona}", content: null);
        return client;
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/dashboard")]
    [InlineData("/domains")]
    [InlineData("/groups")]
    [InlineData("/alerts")]
    [InlineData("/alerts/settings")]
    [InlineData("/access")]
    public async Task APortalUser_IsSentToThePortal_FromEveryInternalPage(string path)
    {
        using var client = await SignInAsync("client");

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/portal", response.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task APortalUser_CanOpenThePortal()
    {
        using var client = await SignInAsync("client");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/portal")).StatusCode);
    }

    [Fact]
    public async Task APortalUser_CantUseTheApi()
    {
        using var client = await SignInAsync("client");

        var response = await client.GetAsync("/api/v1/domains");

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"Got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task Staff_AreSentFromThePortalToTheDashboard()
    {
        using var client = await SignInAsync("viewer");

        var response = await client.GetAsync("/portal");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/dashboard", response.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task Staff_StillReachTheDashboard()
    {
        using var client = await SignInAsync("viewer");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/dashboard")).StatusCode);
    }
}
```

Write out the scaffolding in full, copied from `DemoSignInEndpointTests`.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ClientPortalAccessTests"`
Expected: FAIL (unknown persona `client` gives 400; `/portal` doesn't exist).

- [ ] **Step 3: Implement**

```csharp
// src/DotMarc/Portal/ClientPortalGate.cs
using DotMarc.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

namespace DotMarc.Portal;

/// <summary>Marks a policy as one client portal users may pass. Every other policy fails for them.</summary>
public sealed class ClientPortalRequirement : IAuthorizationRequirement;

/// <summary>Keeps client portal users inside the portal. It runs for every policy (named, fallback, AuthorizeView and
/// API ones alike) and fails any that isn't the portal's own, so a portal user can't open an internal page, call the
/// API or change anything, whatever their role allows. It also satisfies the portal requirement for portal users.</summary>
public sealed class ClientPortalGate : IAuthorizationHandler
{
    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        var isPortalUser = context.User.HasClaim(UserAccessClaimsTransformation.ClientPortalClaimType, "true");
        var portalRequirements = context.PendingRequirements.OfType<ClientPortalRequirement>().ToList();
        var isPortalPolicy = context.Requirements.OfType<ClientPortalRequirement>().Any();

        if (isPortalUser && !isPortalPolicy)
        {
            context.Fail(new AuthorizationFailureReason(this, "Client portal users can only use the portal."));
            return Task.CompletedTask;
        }

        if (isPortalUser)
        {
            foreach (var requirement in portalRequirements)
            {
                context.Succeed(requirement);
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>Where a denied request goes: a portal user to the portal, anyone else denied the portal to the dashboard,
/// everyone else to the usual access denied page.</summary>
public static class ClientPortalRedirects
{
    public static Task OnRedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        var isPortalUser = context.HttpContext.User.HasClaim(UserAccessClaimsTransformation.ClientPortalClaimType, "true");
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        var target = isPortalUser ? "/portal"
            : path.StartsWithSegments("/portal") ? "/dashboard"
            : context.RedirectUri;
        context.Response.Redirect(target);
        return Task.CompletedTask;
    }
}
```

In `Program.cs`:
- In `AddAuthorization`, add `options.AddPolicy("ClientPortal", policy => policy.RequireAuthenticatedUser().AddRequirements(new DotMarc.Portal.ClientPortalRequirement()));`.
- Register `builder.Services.AddSingleton<IAuthorizationHandler, DotMarc.Portal.ClientPortalGate>();`.
- On both cookie registrations (the demo cookie at ~line 333 and the OIDC cookie at ~line 350), set `options.Events.OnRedirectToAccessDenied = DotMarc.Portal.ClientPortalRedirects.OnRedirectToAccessDenied;`. If the OIDC one is configured through `Configure<CookieAuthenticationOptions>(...)`, add the event there.
- Demo sign-in switch: add

```csharp
case "client":
    email = DotMarc.Demo.DemoDataSeeder.ClientEmail;
    displayName = $"Demo Client ({DotMarc.Demo.DemoDataSeeder.ViewerScopedGroupName})";
    break;
```

- The demo sign-in endpoint redirects to `/`; for the client that lands on the fallback policy, is denied by the gate and redirected to `/portal`. Leave it.

The fallback policy requires a permission claim; a portal user with a role still has permission claims, but the gate fails the fallback for them anyway, which is what sends `/` and `/dashboard` to `/portal`.

`DemoDataSeeder`: `public const string ClientEmail = "demo-client@aurora-retail.example";` and add `new UserAccess { Email = ClientEmail, Role = viewerRole, ScopedGroups = [groupsByName[ViewerScopedGroupName]], IsClientPortal = true }` to the seeded grants.

Demo sign-in page: add a third card "Demo Client" ("A client contact for Aurora Retail, who sees the branded client portal.") with a button posting to `/demo/sign-in/client`, matching the existing two.

`PortalHome.razor` placeholder:

```razor
@page "/portal"
@attribute [Authorize(Policy = "ClientPortal")]
@using Microsoft.AspNetCore.Authorization

<PageTitle>Your domains</PageTitle>
<MudText Typo="Typo.h4">Your domains</MudText>
```

- [ ] **Step 4: Run the tests, the suite, and commit**

Run the Step 2 filter (PASS) and `dotnet test test/DotMarc.Tests` (all pass; `ProgramDiValidationTests` covers the handler registration).

```bash
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Keep client portal users inside the portal, and add a demo client"
```

---

### Task 3: Plain-language domain status and the pass-rate trend

**Files:**
- Create: `src/DotMarc/Portal/PortalStatus.cs`, `src/DotMarc/Portal/PortalTrend.cs`
- Test: `test/DotMarc.Tests/Portal/PortalStatusTests.cs`, `test/DotMarc.Tests/Portal/PortalTrendTests.cs`

**Interfaces:**
- Produces:
  - `enum PortalHealth { NeedsAttention, NoReportsYet, MonitoringOnly, Protected }` (ordered so sorting by it puts attention first)
  - `record PortalDomainStatus(PortalHealth Health, IReadOnlyList<string> Reasons)`
  - `PortalStatus.For(Domain domain, bool hasReports, IReadOnlyList<AlertEvent> openAlerts) : PortalDomainStatus`
  - `PortalStatus.Verdict(IReadOnlyList<PortalDomainStatus> statuses) : string`
  - `PortalTrend.DailyPassRates(IEnumerable<Report> reportsInWindow, int days, DateTimeOffset nowUtc) : IReadOnlyList<double?>` (oldest first, null for a day with no mail)

- [ ] **Step 1: Write the failing tests**

```csharp
// test/DotMarc.Tests/Portal/PortalStatusTests.cs
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Portal;
using Xunit;

namespace DotMarc.Tests.Portal;

public sealed class PortalStatusTests
{
    private static Domain HealthyDomain(Action<Domain>? change = null)
    {
        var domain = new Domain
        {
            Name = "aurora-retail.example", FirstSeenUtc = DateTimeOffset.UtcNow,
            DmarcCheckStatus = DmarcCheckStatus.Ok, DmarcPolicy = DmarcPolicyLevel.Reject, DmarcPercent = 100,
            DmarcAuthorizationCheckStatus = DmarcAuthorizationCheckStatus.Ok, SpfCheckStatus = SpfCheckStatus.Ok,
            MxCheckStatus = MxCheckStatus.Ok, DkimCheckStatus = DkimCheckStatus.Ok, TlsrptCheckStatus = TlsrptCheckStatus.Ok,
        };
        change?.Invoke(domain);
        return domain;
    }

    private static AlertEvent OpenAlert(string title) => new() { DomainName = "aurora-retail.example", AlertType = "MissedReport", Severity = "Warning", Title = title, Message = "m" };

    [Fact]
    public void AllChecksPassing_WithAnEnforcingPolicy_IsProtected()
    {
        var status = PortalStatus.For(HealthyDomain(), hasReports: true, []);

        Assert.Equal(new PortalDomainStatus(PortalHealth.Protected, []), status, new PortalDomainStatusComparer());
    }

    [Theory]
    [InlineData(DmarcPolicyLevel.Quarantine, PortalHealth.Protected)]
    [InlineData(DmarcPolicyLevel.None, PortalHealth.MonitoringOnly)]
    public void ThePolicyDecidesBetweenProtectedAndMonitoringOnly(DmarcPolicyLevel policy, PortalHealth expected)
    {
        Assert.Equal(expected, PortalStatus.For(HealthyDomain(domain => domain.DmarcPolicy = policy), hasReports: true, []).Health);
    }

    [Fact]
    public void MonitoringOnly_ExplainsWhatThatMeans()
    {
        var status = PortalStatus.For(HealthyDomain(domain => domain.DmarcPolicy = DmarcPolicyLevel.None), hasReports: true, []);

        Assert.Equal(["The DMARC policy only monitors, so mail spoofing this domain isn't blocked yet."], status.Reasons);
    }

    [Fact]
    public void AFailingCheck_NeedsAttention_AndSaysWhich()
    {
        var status = PortalStatus.For(HealthyDomain(domain => domain.SpfCheckStatus = SpfCheckStatus.MissingRecord), hasReports: true, []);

        Assert.Equal(PortalHealth.NeedsAttention, status.Health);
        Assert.Contains(status.Reasons, reason => reason.StartsWith("SPF:", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOpenAlert_NeedsAttention_AndIsListed()
    {
        var status = PortalStatus.For(HealthyDomain(), hasReports: true, [OpenAlert("Missing expected DMARC report")]);

        Assert.Equal(PortalHealth.NeedsAttention, status.Health);
        Assert.Contains("Missing expected DMARC report", status.Reasons);
    }

    [Fact]
    public void NoReports_AndNothingElseWrong_IsNoReportsYet()
    {
        var status = PortalStatus.For(HealthyDomain(), hasReports: false, []);

        Assert.Equal(PortalHealth.NoReportsYet, status.Health);
        Assert.Equal(["No DMARC reports have arrived yet. They usually start within a few days."], status.Reasons);
    }

    [Fact]
    public void ChecksNotRunYet_OrNotConfigured_DontNeedAttention()
    {
        var domain = HealthyDomain(candidate =>
        {
            candidate.SpfCheckStatus = SpfCheckStatus.NotChecked;
            candidate.DkimCheckStatus = DkimCheckStatus.NotConfigured;
            candidate.DmarcAuthorizationCheckStatus = DmarcAuthorizationCheckStatus.NotApplicable;
        });

        Assert.Equal(PortalHealth.Protected, PortalStatus.For(domain, hasReports: true, []).Health);
    }

    [Fact]
    public void AFailedMtaStsHosting_NeedsAttention_ButNotHostingDoesnt()
    {
        Assert.Equal(PortalHealth.NeedsAttention, PortalStatus.For(HealthyDomain(domain => domain.MtaStsStatus = MtaStsStatus.Failed), true, []).Health);
        Assert.Equal(PortalHealth.Protected, PortalStatus.For(HealthyDomain(domain => domain.MtaStsStatus = MtaStsStatus.NotConfigured), true, []).Health);
    }

    [Theory]
    [InlineData(new[] { PortalHealth.Protected, PortalHealth.Protected, PortalHealth.Protected }, "All 3 domains are protected.")]
    [InlineData(new[] { PortalHealth.Protected }, "Your domain is protected.")]
    [InlineData(new[] { PortalHealth.Protected, PortalHealth.Protected, PortalHealth.NeedsAttention }, "2 of 3 domains are fully protected. 1 needs attention.")]
    [InlineData(new[] { PortalHealth.MonitoringOnly, PortalHealth.NeedsAttention }, "0 of 2 domains are fully protected. 1 needs attention.")]
    public void TheVerdict_SumsUpTheDomains(PortalHealth[] healths, string expected)
    {
        Assert.Equal(expected, PortalStatus.Verdict(healths.Select(health => new PortalDomainStatus(health, [])).ToList()));
    }

    [Fact]
    public void TheVerdict_ForNoDomains_SaysSo()
    {
        Assert.Equal("There are no domains to show yet.", PortalStatus.Verdict([]));
    }

    private sealed class PortalDomainStatusComparer : IEqualityComparer<PortalDomainStatus>
    {
        public bool Equals(PortalDomainStatus? left, PortalDomainStatus? right) =>
            left is not null && right is not null && left.Health == right.Health && left.Reasons.SequenceEqual(right.Reasons);

        public int GetHashCode(PortalDomainStatus status) => status.Health.GetHashCode();
    }
}
```

```csharp
// test/DotMarc.Tests/Portal/PortalTrendTests.cs
using DotMarc.Data;
using DotMarc.Portal;
using Xunit;

namespace DotMarc.Tests.Portal;

public sealed class PortalTrendTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A report received on the given day with a passing record (SPF passes) and a failing one (both fail),
    /// which is the rule DomainStatistics.GetPassRate uses.</summary>
    private static Report ReportOn(DateTimeOffset receivedUtc, int passing, int failing) => new()
    {
        ReportingOrg = "google.com",
        ReportId = Guid.NewGuid().ToString(),
        RawXml = "<feedback/>",
        ReceivedUtc = receivedUtc,
        Records =
        [
            new ReportRecord { SourceIp = "192.0.2.1", HeaderFrom = "aurora-retail.example", MessageCount = passing, SpfResult = AuthResult.Pass, DkimResult = AuthResult.Fail },
            new ReportRecord { SourceIp = "198.51.100.7", HeaderFrom = "aurora-retail.example", MessageCount = failing, SpfResult = AuthResult.Fail, DkimResult = AuthResult.Fail },
        ],
    };

    [Fact]
    public void EachDay_GetsItsOwnPassRate_OldestFirst_AndADayWithNoMailIsNull()
    {
        var reports = new[]
        {
            ReportOn(Now.AddDays(-2), passing: 9, failing: 1),
            ReportOn(Now, passing: 1, failing: 1),
        };

        var rates = PortalTrend.DailyPassRates(reports, days: 3, Now);

        Assert.Equal([0.9, null, 0.5], rates);
    }
}
```

`GetPassRate` counts a record as passing when SPF or DKIM passes, weighted by `MessageCount`, and returns a fraction from 0 to 1.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PortalStatusTests|FullyQualifiedName~PortalTrendTests"`
Expected: build FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/DotMarc/Portal/PortalStatus.cs
using System.Globalization;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Reporting;

namespace DotMarc.Portal;

/// <summary>Attention first, so sorting by it lists the domains that need something at the top.</summary>
public enum PortalHealth { NeedsAttention, NoReportsYet, MonitoringOnly, Protected }

public sealed record PortalDomainStatus(PortalHealth Health, IReadOnlyList<string> Reasons);

/// <summary>Turns a domain's health checks, reports and open alerts into the plain-language status a client sees.</summary>
public static class PortalStatus
{
    public const string MonitoringOnlyReason = "The DMARC policy only monitors, so mail spoofing this domain isn't blocked yet.";
    public const string NoReportsYetReason = "No DMARC reports have arrived yet. They usually start within a few days.";

    public static PortalDomainStatus For(Domain domain, bool hasReports, IReadOnlyList<AlertEvent> openAlerts)
    {
        var problems = FailingChecks(domain).Concat(openAlerts.Select(alert => alert.Title)).Distinct().ToList();
        if (problems.Count > 0)
        {
            return new PortalDomainStatus(PortalHealth.NeedsAttention, problems);
        }

        if (!hasReports)
        {
            return new PortalDomainStatus(PortalHealth.NoReportsYet, [NoReportsYetReason]);
        }

        return domain.DmarcPolicy is DmarcPolicyLevel.Quarantine or DmarcPolicyLevel.Reject
            ? new PortalDomainStatus(PortalHealth.Protected, [])
            : new PortalDomainStatus(PortalHealth.MonitoringOnly, [MonitoringOnlyReason]);
    }

    /// <summary>One line per failing check, "Check: what's wrong", using the labels the app already shows. Checks not run
    /// yet, not configured, or not applicable aren't failures.</summary>
    public static IEnumerable<string> FailingChecks(Domain domain)
    {
        if (domain.DmarcCheckStatus is DmarcCheckStatus.MissingOwnRecord or DmarcCheckStatus.Misconfigured or DmarcCheckStatus.MissingAuthorizationRecord)
            yield return $"DMARC: {DmarcStatusPresentation.GetLabel(domain.DmarcCheckStatus)}";
        if (domain.DmarcAuthorizationCheckStatus == DmarcAuthorizationCheckStatus.Missing)
            yield return $"DMARC reporting: {DmarcAuthorizationStatusPresentation.GetLabel(domain.DmarcAuthorizationCheckStatus)}";
        if (domain.SpfCheckStatus is not (SpfCheckStatus.NotChecked or SpfCheckStatus.Ok or SpfCheckStatus.NullSpf))
            yield return $"SPF: {SpfStatusPresentation.GetLabel(domain.SpfCheckStatus)}";
        if (domain.MxCheckStatus is MxCheckStatus.MissingRecord or MxCheckStatus.UnresolvableTarget)
            yield return $"MX: {MxStatusPresentation.GetLabel(domain.MxCheckStatus)}";
        if (domain.DkimCheckStatus is DkimCheckStatus.Missing or DkimCheckStatus.Misconfigured)
            yield return $"DKIM: {DkimStatusPresentation.GetLabel(domain.DkimCheckStatus)}";
        if (domain.TlsrptCheckStatus is TlsrptCheckStatus.MissingOwnRecord or TlsrptCheckStatus.Misconfigured)
            yield return $"TLS reporting: {TlsrptStatusPresentation.GetLabel(domain.TlsrptCheckStatus)}";
        if (domain.MtaStsStatus == MtaStsStatus.Failed)
            yield return $"MTA-STS: {MtaStsStatusPresentation.GetLabel(domain.MtaStsStatus)}";
    }

    public static string Verdict(IReadOnlyList<PortalDomainStatus> statuses)
    {
        if (statuses.Count == 0)
        {
            return "There are no domains to show yet.";
        }

        var protectedCount = statuses.Count(status => status.Health == PortalHealth.Protected);
        var attentionCount = statuses.Count(status => status.Health == PortalHealth.NeedsAttention);
        if (protectedCount == statuses.Count)
        {
            return statuses.Count == 1 ? "Your domain is protected." : string.Create(CultureInfo.InvariantCulture, $"All {statuses.Count} domains are protected.");
        }

        var sentence = string.Create(CultureInfo.InvariantCulture, $"{protectedCount} of {statuses.Count} domains are fully protected.");
        return attentionCount == 0 ? sentence : string.Create(CultureInfo.InvariantCulture, $"{sentence} {attentionCount} needs attention.");
    }
}
```

Check: `domain.MtaStsStatus` is the property name on `Domain` for MTA-STS (search `MtaStsStatus` in `Data/Domain.cs`). The single-domain-needs-attention verdict "0 of 1 domains are fully protected. 1 needs attention." is accepted English for this purpose; leave it.

```csharp
// src/DotMarc/Portal/PortalTrend.cs
using DotMarc.Data;
using DotMarc.Reporting;

namespace DotMarc.Portal;

public static class PortalTrend
{
    /// <summary>The DMARC pass rate for each of the last <paramref name="days"/> days (UTC), oldest first. A day with no
    /// mail reported is null, so the trend line shows a gap instead of a misleading zero.</summary>
    public static IReadOnlyList<double?> DailyPassRates(IEnumerable<Report> reportsInWindow, int days, DateTimeOffset nowUtc)
    {
        var today = nowUtc.UtcDateTime.Date;
        var byDay = reportsInWindow.GroupBy(report => report.ReceivedUtc.UtcDateTime.Date).ToDictionary(sameDay => sameDay.Key, sameDay => sameDay.ToList());
        return Enumerable.Range(0, days)
            .Select(offset => today.AddDays(offset - days + 1))
            .Select(day => byDay.TryGetValue(day, out var reports) ? DomainStatistics.GetPassRate(reports) : null)
            .ToList();
    }
}
```

(`DomainStatistics.GetPassRate` returns a fraction from 0 to 1, or null when there's no mail.)

- [ ] **Step 4: Run them and commit**

Run the Step 2 filter (PASS), then `dotnet test test/DotMarc.Tests`.

```bash
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Describe each domain's status for clients in plain language"
```

---

### Task 4: Portal layout, home page, domain page and the Access page switch

**Files:**
- Create: `src/DotMarc/Portal/PortalData.cs`
- Create: `src/DotMarc/Components/Layout/PortalLayout.razor`
- Modify: `src/DotMarc/Components/Pages/Portal/PortalHome.razor`
- Create: `src/DotMarc/Components/Pages/Portal/PortalDomain.razor`, `src/DotMarc/Components/Portal/PortalDomainCard.razor`, `src/DotMarc/Components/Portal/PortalTrendLine.razor`
- Modify: `src/DotMarc/Components/Pages/ManageAccess.razor`
- Modify: `src/DotMarc/Program.cs` (register `PortalData` scoped)
- Test: `test/DotMarc.Tests/Portal/PortalDataTests.cs`, extend `ClientPortalAccessTests.cs`

**Interfaces:**
- Consumes: `PortalStatus`, `PortalTrend`, `UserAccessClaimsTransformation.ScopedGroupClaimType`, `ClientPortalClaimType`.
- Produces:
  - `PortalData(IDbContextFactory<DotMarcDbContext> dbFactory, TimeProvider timeProvider)` with
    - `Task<IReadOnlyList<PortalDomainSummary>> ListDomainsAsync(IReadOnlyCollection<int> groupIds, CancellationToken ct = default)`
    - `Task<PortalDomainDetail?> GetDomainAsync(IReadOnlyCollection<int> groupIds, string domainName, CancellationToken ct = default)`
    - `static IReadOnlyCollection<int> ScopedGroupIds(ClaimsPrincipal user)`: the user's scoped-group ids; empty for anyone without scoped-group claims (portal users always have them; this never means "all")
  - `record PortalDomainSummary(string Name, PortalDomainStatus Status, double? PassRate, IReadOnlyList<double?> Trend, IReadOnlyList<AlertEvent> OpenAlerts)`
  - `record PortalDomainDetail(PortalDomainSummary Summary, Domain Domain, IReadOnlyList<SourceAggregate> TopSources, IReadOnlyList<AlertEvent> RecentAlerts)`
  - `PortalLayout` with `[CascadingParameter]`-free brand handling: in this task the brand is fixed dotMARC defaults (product name "dotMARC", the existing theme); Task 7 swaps in the resolved brand.

- [ ] **Step 1: Write the failing data tests**

```csharp
// test/DotMarc.Tests/Portal/PortalDataTests.cs
[Collection("Postgres")]
public sealed class PortalDataTests : IAsyncLifetime
{
    // Scaffolding as PsaTicketServiceTests (database, migrate, CreateContext).

    private PortalData CreatePortalData() => new(new FakeDbContextFactory(_connectionString), TimeProvider.System);

    private async Task<(int AuroraGroupId, int OtherGroupId)> SeedAsync()
    {
        await using var context = CreateContext();
        var aurora = new Group { Name = "Aurora Retail" };
        var other = new Group { Name = "Brightline Legal" };
        context.Domains.AddRange(
            new Domain { Name = "aurora-retail.example", FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = true, Groups = [aurora], DmarcPolicy = DmarcPolicyLevel.Reject },
            new Domain { Name = "shop.aurora-retail.example", FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = true, Groups = [aurora], SpfCheckStatus = SpfCheckStatus.MissingRecord },
            new Domain { Name = "not-monitored.aurora-retail.example", FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = false, Groups = [aurora] },
            new Domain { Name = "brightline-legal.example", FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = true, Groups = [other] });
        context.AlertEvents.Add(new AlertEvent { DomainName = "shop.aurora-retail.example", AlertType = "SpfRecordBroken", Severity = "Warning", Title = "SPF record broken", Message = "m" });
        await context.SaveChangesAsync();
        return (aurora.Id, other.Id);
    }

    [Fact]
    public async Task ListDomains_ShowsOnlyMonitoredDomainsInTheGivenGroups_AttentionFirst()
    {
        var (auroraGroupId, _) = await SeedAsync();

        var domains = await CreatePortalData().ListDomainsAsync([auroraGroupId]);

        Assert.Equal(["shop.aurora-retail.example", "aurora-retail.example"], domains.Select(domain => domain.Name));
        Assert.Equal(PortalHealth.NeedsAttention, domains[0].Status.Health);
        Assert.Equal("SPF record broken", Assert.Single(domains[0].OpenAlerts).Title);
    }

    [Fact]
    public async Task ListDomains_WithNoGroups_ShowsNothing()
    {
        await SeedAsync();

        Assert.Empty(await CreatePortalData().ListDomainsAsync([]));
    }

    [Fact]
    public async Task GetDomain_OutsideTheGroups_IsNull()
    {
        var (auroraGroupId, _) = await SeedAsync();

        Assert.Null(await CreatePortalData().GetDomainAsync([auroraGroupId], "brightline-legal.example"));
        Assert.NotNull(await CreatePortalData().GetDomainAsync([auroraGroupId], "aurora-retail.example"));
    }

    [Fact]
    public void ScopedGroupIds_WithoutScopeClaims_IsEmpty_NeverEverything()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(UserAccessClaimsTransformation.ClientPortalClaimType, "true")], "Test"));

        Assert.Empty(PortalData.ScopedGroupIds(user));
    }
}
```

Extend `ClientPortalAccessTests`:

```csharp
[Fact]
public async Task ThePortal_ListsTheClientsDomains_AndNotOthers()
{
    using var client = await SignInAsync("client");

    var html = await client.GetStringAsync("/portal");

    Assert.Contains("aurora-retail.example", html);
    Assert.DoesNotContain("brightline-legal.example", html);
}

[Fact]
public async Task AnotherGroupsDomainPage_IsNotFound()
{
    using var client = await SignInAsync("client");

    var html = await client.GetStringAsync("/portal/domains/brightline-legal.example");

    Assert.Contains("We couldn't find that domain", html);
    Assert.DoesNotContain("Who sends as this domain", html);
}
```

(Pages are prerendered, so the HTML contains the rendered content; check the demo dataset's actual domain names in `DemoDataGenerator` and use one from Aurora Retail and one from another Group.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PortalDataTests|FullyQualifiedName~ClientPortalAccessTests"`
Expected: build FAIL (`PortalData` missing).

- [ ] **Step 3: Implement `PortalData`**

```csharp
// src/DotMarc/Portal/PortalData.cs
using System.Globalization;
using System.Security.Claims;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Reporting;
using DotMarc.Security;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Portal;

public sealed record PortalDomainSummary(string Name, PortalDomainStatus Status, double? PassRate, IReadOnlyList<double?> Trend, IReadOnlyList<AlertEvent> OpenAlerts);

public sealed record PortalDomainDetail(PortalDomainSummary Summary, Domain Domain, IReadOnlyList<SourceAggregate> TopSources, IReadOnlyList<AlertEvent> RecentAlerts);

/// <summary>Loads what the portal shows, always limited to the given Groups. An empty set of Groups shows nothing: unlike
/// elsewhere in dotMARC, "no Groups" never means "every domain" here.</summary>
public sealed class PortalData(IDbContextFactory<DotMarcDbContext> dbFactory, TimeProvider timeProvider)
{
    private const int TrendDays = 30;
    private const int TopSourceCount = 10;

    public static IReadOnlyCollection<int> ScopedGroupIds(ClaimsPrincipal user) =>
        user.FindAll(UserAccessClaimsTransformation.ScopedGroupClaimType)
            .Select(claim => int.Parse(claim.Value, CultureInfo.InvariantCulture))
            .ToHashSet();

    public async Task<IReadOnlyList<PortalDomainSummary>> ListDomainsAsync(IReadOnlyCollection<int> groupIds, CancellationToken cancellationToken = default)
    {
        if (groupIds.Count == 0)
        {
            return [];
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var domains = await DomainsWithReports(context, groupIds).ToListAsync(cancellationToken).ConfigureAwait(false);
        var openAlerts = await OpenAlertsAsync(context, domains.Select(domain => domain.Name).ToList(), cancellationToken).ConfigureAwait(false);
        return domains
            .Select(domain => Summarise(domain, openAlerts[domain.Name]))
            .OrderBy(summary => summary.Status.Health)
            .ThenBy(summary => summary.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<PortalDomainDetail?> GetDomainAsync(IReadOnlyCollection<int> groupIds, string domainName, CancellationToken cancellationToken = default)
    {
        if (groupIds.Count == 0)
        {
            return null;
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var domain = await DomainsWithReports(context, groupIds).SingleOrDefaultAsync(candidate => candidate.Name == domainName, cancellationToken).ConfigureAwait(false);
        if (domain is null)
        {
            return null;
        }

        var openAlerts = await OpenAlertsAsync(context, [domain.Name], cancellationToken).ConfigureAwait(false);
        var recentCutoff = timeProvider.GetUtcNow().AddDays(-30);
        var recentAlerts = await context.AlertEvents.AsNoTracking()
            .Where(alert => alert.DomainName == domain.Name && (!alert.IsResolved || alert.ResolvedUtc >= recentCutoff))
            .OrderByDescending(alert => alert.CreatedUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var topSources = DomainStatistics.GetSourceAggregates(domain.Reports).OrderByDescending(source => source.Volume).Take(TopSourceCount).ToList();
        return new PortalDomainDetail(Summarise(domain, openAlerts[domain.Name]), domain, topSources, recentAlerts);
    }

    private IQueryable<Domain> DomainsWithReports(DotMarcDbContext context, IReadOnlyCollection<int> groupIds)
    {
        var cutoff = DomainStatistics.GetWindowCutoffUtc(timeProvider.GetUtcNow());
        return context.Domains.AsNoTracking()
            .Where(domain => domain.IsMonitored && domain.Groups.Any(group => groupIds.Contains(group.Id)))
            .Include(domain => domain.Reports.Where(report => report.ReceivedUtc >= cutoff)).ThenInclude(report => report.Records).ThenInclude(record => record.OverrideReasons)
            .Include(domain => domain.Reports.Where(report => report.ReceivedUtc >= cutoff)).ThenInclude(report => report.Records).ThenInclude(record => record.AuthDetails)
            .AsSplitQuery();
    }

    private static async Task<ILookup<string, AlertEvent>> OpenAlertsAsync(DotMarcDbContext context, IReadOnlyList<string> domainNames, CancellationToken cancellationToken) =>
        (await context.AlertEvents.AsNoTracking()
            .Where(alert => !alert.IsResolved && domainNames.Contains(alert.DomainName))
            .OrderByDescending(alert => alert.CreatedUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        .ToLookup(alert => alert.DomainName);

    private PortalDomainSummary Summarise(Domain domain, IEnumerable<AlertEvent> openAlerts)
    {
        var alerts = openAlerts.ToList();
        return new PortalDomainSummary(
            domain.Name,
            PortalStatus.For(domain, domain.LastReportReceivedUtc is not null, alerts),
            DomainStatistics.GetPassRate(domain.Reports),
            PortalTrend.DailyPassRates(domain.Reports, TrendDays, timeProvider.GetUtcNow()),
            alerts);
    }
}
```

(Check `DomainStatistics.GetWindowCutoffUtc` takes a nullable `DateTimeOffset`; pass the value. Register `builder.Services.AddScoped<DotMarc.Portal.PortalData>();`.)

- [ ] **Step 4: Layout and pages**

`PortalLayout.razor` (`@inherits LayoutComponentBase`): `MudThemeProvider` with a theme built from dotMARC's existing palette (copy `MainLayout`'s `_theme` into a static `PortalTheme.Default` property in `src/DotMarc/Portal/PortalTheme.cs` so both use one source; Task 7 adds `PortalTheme.For(brand)`), the popover, dialog and snackbar providers, and:
- `MudAppBar`: the dotMARC logo (`@Assets["logo.svg"]`) linking to `/portal`, the product name "dotMARC", a spacer, a dark-mode toggle (reuse `MainLayout`'s dark-mode JS approach: read `MainLayout.razor` and copy its toggle and persistence code), and a Sign out button posting to the same sign-out endpoint `MainLayout` uses.
- `MudMainContent` with a `MudContainer MaxWidth="MaxWidth.Large"` around `@Body`.
- A footer line (empty in this task; Task 7 adds support contacts).

`PortalHome.razor`:

```razor
@page "/portal"
@layout DotMarc.Components.Layout.PortalLayout
@attribute [Authorize(Policy = "ClientPortal")]
@using DotMarc.Portal
@using Microsoft.AspNetCore.Authorization
@inject PortalData PortalData
@inject AuthenticationStateProvider AuthenticationStateProvider

<PageTitle>Your domains</PageTitle>

@if (_domains is null)
{
    <MudProgressCircular Indeterminate="true" />
}
else
{
    <MudText Typo="Typo.h4" Class="mb-1">Your domains</MudText>
    <MudText Typo="Typo.subtitle1" Class="mb-4">@PortalStatus.Verdict(_domains.Select(domain => domain.Status).ToList())</MudText>
    @if (_domains.Count == 0)
    {
        <MudAlert Severity="Severity.Info">There are no domains to show yet. If you expected some, contact your provider.</MudAlert>
    }
    <MudGrid>
        @foreach (var domain in _domains)
        {
            <MudItem xs="12" md="6"><PortalDomainCard Summary="domain" /></MudItem>
        }
    </MudGrid>
}

@code {
    private IReadOnlyList<PortalDomainSummary>? _domains;

    protected override async Task OnInitializedAsync()
    {
        var user = (await AuthenticationStateProvider.GetAuthenticationStateAsync()).User;
        _domains = await PortalData.ListDomainsAsync(PortalData.ScopedGroupIds(user));
    }
}
```

`PortalDomainCard.razor` (`[Parameter, EditorRequired] PortalDomainSummary Summary`): a `MudCard` with the domain name as a link to `/portal/domains/{Uri.EscapeDataString(name)}`, a `MudChip` for the health (Protected: Success, "Protected"; MonitoringOnly: Info, "Monitoring only"; NoReportsYet: Default, "No reports yet"; NeedsAttention: Warning, "Needs attention"), the reasons as a short list, "DMARC pass rate (30 days): {rate:P0}" or "No mail reported in the last 30 days", `<PortalTrendLine Rates="Summary.Trend" />`, and "{n} open alert(s)" when there are any.

`PortalTrendLine.razor` (`[Parameter] IReadOnlyList<double?> Rates`): an inline SVG 120 by 24 drawing a polyline through the non-null points (x evenly spaced, y = 24 - rate * 24), broken into separate polylines at nulls, stroke `var(--mud-palette-primary)`, with a `<title>` "Daily DMARC pass rate over the last 30 days" for screen readers. No chart library.

`PortalDomain.razor` (`@page "/portal/domains/{DomainName}"`, same layout and policy): loads `PortalData.GetDomainAsync(scopedGroupIds, DomainName)`. Null shows `<MudAlert Severity="Severity.Info">We couldn't find that domain. It may have been removed, or it isn't one of yours.</MudAlert>` and a link back. Otherwise sections:
- heading: the domain name, the health chip, and the reasons;
- **Policy**: one sentence built from `Domain.DmarcPolicy`, `DmarcSubdomainPolicy` and `DmarcPercent`, for example "Mail that fails DMARC is rejected (100% of it). Subdomains: rejected." or "Mail that fails DMARC is only reported, not blocked." or "No DMARC policy was found.";
- **Health**: a `MudSimpleTable` row per check (DMARC, DMARC reporting, SPF, MX, DKIM, TLS reporting, MTA-STS when not NotConfigured) with the label from the matching `*StatusPresentation.GetLabel` and a coloured dot from `GetColor`;
- **Who sends as this domain** (last 30 days): a table of the top sources (source IP, messages, SPF result, DKIM result, disposition);
- **Alerts**: open and recent alerts (title, raised, resolved or "Open").

`ManageAccess.razor`: add a **Client portal** column to the grants table. For a grant whose role is scopable, a `MudSwitch` bound to the grant's `IsClientPortal` (add it to `GrantRow`) calling `UserAccessManagementService.SetClientPortalAsync`; on `NeedsScopedGroups` show the snackbar "A client portal grant must be limited to the client's Groups." and reload. For other roles show "Not available". Add `FieldWithHelp` help on the column header: "This person sees a simplified, branded view of their Groups' domains instead of dotMARC. Use it for client contacts."

- [ ] **Step 5: Run the tests, browser check, commit**

Run the Step 2 filter (PASS), then `dotnet test test/DotMarc.Tests` (all pass).

Browser check (playwright-edge, demo mode; start with `Demo__Enabled=true`, `ASPNETCORE_URLS=http://localhost:54704`, `dotnet exec bin/Debug/net10.0/DotMarc.dll` from `src/DotMarc`): sign in as Demo Client; the portal home shows Aurora Retail's domains with chips, pass rates and trend lines; a domain page shows all four sections; `/dashboard` lands back on `/portal`. Sign in as Demo Admin; the Access page shows the switch on the client's grant. Stop only your own process.

```bash
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Add the client portal home and domain pages, and the Access page switch"
```

---

## Phase 2: MSP branding

### Task 5: Brand colours and logo validation

**Files:**
- Create: `src/DotMarc/Portal/BrandColours.cs`, `src/DotMarc/Portal/BrandingImages.cs`
- Test: `test/DotMarc.Tests/Portal/BrandColoursTests.cs`, `test/DotMarc.Tests/Portal/BrandingImagesTests.cs`

**Interfaces:**
- Produces:
  - `BrandColours.IsValid(string? value) : bool`, `BrandColours.ContrastRatio(string first, string second) : double`, `BrandColours.MinimumTextContrast = 4.5`
  - `BrandingImages.MaximumBytes = 512 * 1024`, `BrandingImages.Validate(byte[] bytes) : BrandingImageCheck`, `record BrandingImageCheck(string? ContentType, string? Problem)` (exactly one of the two is set)

- [ ] **Step 1: Write the failing tests**

```csharp
// test/DotMarc.Tests/Portal/BrandColoursTests.cs
public sealed class BrandColoursTests
{
    [Theory]
    [InlineData("#1A2B3C", true)]
    [InlineData("#abcdef", true)]
    [InlineData("1A2B3C", false)]
    [InlineData("#FFF", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData(null, false)]
    public void OnlySixDigitHexWithAHash_IsValid(string? value, bool expected) => Assert.Equal(expected, BrandColours.IsValid(value));

    [Fact]
    public void BlackOnWhite_Is21To1_AndAColourAgainstItself_Is1To1()
    {
        Assert.Equal(21, BrandColours.ContrastRatio("#000000", "#FFFFFF"), precision: 2);
        Assert.Equal(1, BrandColours.ContrastRatio("#3366CC", "#3366CC"), precision: 2);
    }
}
```

```csharp
// test/DotMarc.Tests/Portal/BrandingImagesTests.cs
using System.Text;
using DotMarc.Portal;
using Xunit;

namespace DotMarc.Tests.Portal;

public sealed class BrandingImagesTests
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0];

    private static byte[] Svg(string inner) => Encoding.UTF8.GetBytes($"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 10 10\">{inner}</svg>");

    [Fact]
    public void PngAndJpeg_AreRecognisedByTheirContent()
    {
        Assert.Equal("image/png", BrandingImages.Validate(PngHeader).ContentType);
        Assert.Equal("image/jpeg", BrandingImages.Validate(JpegHeader).ContentType);
    }

    [Fact]
    public void APlainSvg_IsAccepted()
    {
        var check = BrandingImages.Validate(Svg("<rect width=\"10\" height=\"10\" fill=\"#123456\"/><use href=\"#a\"/><image href=\"data:image/png;base64,AAAA\"/>"));

        Assert.Equal(("image/svg+xml", (string?)null), (check.ContentType, check.Problem));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<rect onload=\"alert(1)\"/>")]
    [InlineData("<foreignObject><div/></foreignObject>")]
    [InlineData("<image href=\"https://evil.example/x.png\"/>")]
    [InlineData("<a xlink:href=\"javascript:alert(1)\"><text>x</text></a>")]
    [InlineData("<use href=\"other.svg#a\"/>")]
    public void AnSvgThatCouldRunCodeOrFetchSomething_IsRefused(string inner)
    {
        var check = BrandingImages.Validate(Svg(inner));

        Assert.Null(check.ContentType);
        Assert.Equal("This SVG contains scripts or external links, so it can't be used.", check.Problem);
    }

    [Fact]
    public void AnSvgWithADoctype_IsRefused()
    {
        var withEntity = Encoding.UTF8.GetBytes("<!DOCTYPE svg [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><svg xmlns=\"http://www.w3.org/2000/svg\">&x;</svg>");

        Assert.NotNull(BrandingImages.Validate(withEntity).Problem);
    }

    [Fact]
    public void AnythingElse_OrTooLarge_IsRefused()
    {
        Assert.Equal("Logos must be PNG, JPEG or SVG, up to 512 KB.", BrandingImages.Validate(Encoding.UTF8.GetBytes("GIF89a")).Problem);

        var tooLarge = new byte[BrandingImages.MaximumBytes + 1];
        PngHeader.CopyTo(tooLarge, 0);
        Assert.Equal("Logos must be PNG, JPEG or SVG, up to 512 KB.", BrandingImages.Validate(tooLarge).Problem);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~BrandColoursTests|FullyQualifiedName~BrandingImagesTests"`
Expected: build FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/DotMarc/Portal/BrandColours.cs
using System.Globalization;
using System.Text.RegularExpressions;

namespace DotMarc.Portal;

public static partial class BrandColours
{
    /// <summary>WCAG AA for normal text.</summary>
    public const double MinimumTextContrast = 4.5;

    public static bool IsValid(string? value) => value is not null && HexColour().IsMatch(value);

    /// <summary>The WCAG contrast ratio between two #RRGGBB colours, from 1 (none) to 21 (black on white).</summary>
    public static double ContrastRatio(string first, string second)
    {
        var lighter = Math.Max(Luminance(first), Luminance(second));
        var darker = Math.Min(Luminance(first), Luminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(string colour)
    {
        double Channel(int offset)
        {
            var value = int.Parse(colour.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColour();
}
```

```csharp
// src/DotMarc/Portal/BrandingImages.cs
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DotMarc.Portal;

public sealed record BrandingImageCheck(string? ContentType, string? Problem);

/// <summary>Decides whether uploaded bytes can be a logo. The type comes from the content, never the file name. SVGs are
/// parsed and refused if they could run code or fetch anything, because the portal is shown to people outside the MSP.</summary>
public static class BrandingImages
{
    public const int MaximumBytes = 512 * 1024;
    public const string WrongTypeOrSize = "Logos must be PNG, JPEG or SVG, up to 512 KB.";
    public const string UnsafeSvg = "This SVG contains scripts or external links, so it can't be used.";

    public static BrandingImageCheck Validate(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumBytes)
        {
            return new BrandingImageCheck(null, WrongTypeOrSize);
        }

        if (bytes.AsSpan().StartsWith((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return new BrandingImageCheck("image/png", null);
        }

        if (bytes.AsSpan().StartsWith((byte[])[0xFF, 0xD8, 0xFF]))
        {
            return new BrandingImageCheck("image/jpeg", null);
        }

        return ValidateSvg(bytes);
    }

    private static BrandingImageCheck ValidateSvg(byte[] bytes)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            // Not XML at all is the wrong type; XML with a DTD is refused as unsafe below by the same message.
            return Encoding.UTF8.GetString(bytes).Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                ? new BrandingImageCheck(null, UnsafeSvg)
                : new BrandingImageCheck(null, WrongTypeOrSize);
        }

        if (document.Root?.Name.LocalName != "svg")
        {
            return new BrandingImageCheck(null, WrongTypeOrSize);
        }

        foreach (var element in document.Root.DescendantsAndSelf())
        {
            if (element.Name.LocalName is "script" or "foreignObject")
            {
                return new BrandingImageCheck(null, UnsafeSvg);
            }

            foreach (var attribute in element.Attributes())
            {
                var name = attribute.Name.LocalName;
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || (name == "href" && !(attribute.Value.StartsWith('#') || attribute.Value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))))
                {
                    return new BrandingImageCheck(null, UnsafeSvg);
                }
            }
        }

        return new BrandingImageCheck("image/svg+xml", null);
    }
}
```

(`href` covers both plain `href` and `xlink:href`, since `LocalName` drops the prefix. A DOCTYPE makes `XmlReader` throw with `DtdProcessing.Prohibit`, which the catch turns into the unsafe message.)

- [ ] **Step 4: Run them and commit**

Run the Step 2 filter (PASS), then `dotnet test test/DotMarc.Tests`.

```bash
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Validate brand colours and logo uploads"
```

---

### Task 6: MSP branding settings, logo storage and serving

**Files:**
- Create: `src/DotMarc/Portal/BrandingSettings.cs`, `src/DotMarc/Portal/BrandingImage.cs`, `src/DotMarc/Portal/BrandingSettingsService.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs`, `src/DotMarc/Audit/AuditActions.cs`, `src/DotMarc/Program.cs` (logo endpoint), `test/DotMarc.Tests/Audit/AuditCoverageTests.cs`, `src/DotMarc/Demo/DemoDataSeeder.cs` (truncate list)
- Create: migration `AddBranding`
- Test: `test/DotMarc.Tests/Portal/BrandingSettingsServiceTests.cs`, `test/DotMarc.Tests/Portal/BrandingLogoEndpointTests.cs`

**Interfaces:**
- Consumes: `BrandColours`, `BrandingImages`.
- Produces:
  - `BrandingSettings { Id; ProductName = "dotMARC"; PrimaryColour; SecondaryColour; Guid? LogoImageId; Guid? DarkLogoImageId; SupportEmail; SupportUrl; SupportPhone; FooterText }` with `DefaultPrimaryColour` and `DefaultSecondaryColour` constants equal to `PortalTheme.Default`'s palette values
  - `BrandingImage { Guid Id; string ContentType; byte[] Bytes; string Sha256; DateTimeOffset UploadedUtc }`
  - `BrandingSettingsService.GetAsync(context, ct)`, `BrandingSettingsService.SaveAsync(context, actor, BrandingSettings updated, ct)` (throws `ArgumentException` with a field message on invalid input), `BrandingSettingsService.UploadImageAsync(context, actor, byte[] bytes, ct) : Task<BrandingImageUpload>` with `record BrandingImageUpload(Guid? ImageId, string? Problem)`
  - `BrandingImageCleanup.DeleteUnreferencedAsync(DotMarcDbContext context, CancellationToken ct)` (internal helper, not on the audited service)
  - `AuditActions.BrandingSettingsSaved = "settings.branding.saved"` ("Branding saved")
  - endpoint `GET /branding/logo/{id:guid}`

- [ ] **Step 1: Write the failing tests**

`BrandingSettingsServiceTests` (Postgres; scaffolding as other settings service tests):

```csharp
[Fact]
public async Task Save_StoresTheBrand_AndAuditsTheChanges()
{
    await using var context = CreateContext();
    var updated = await BrandingSettingsService.GetAsync(context);
    updated.ProductName = "Nova MSP";
    updated.PrimaryColour = "#0B5FFF";
    updated.SupportEmail = "help@nova-msp.example";

    await BrandingSettingsService.SaveAsync(context, TestActors.Admin, updated);

    await using var verify = CreateContext();
    var saved = await BrandingSettingsService.GetAsync(verify);
    Assert.Equal(("Nova MSP", "#0B5FFF", "help@nova-msp.example"), (saved.ProductName, saved.PrimaryColour, saved.SupportEmail));
    var entry = await verify.AuditEntries.SingleAsync();
    Assert.Equal(AuditActions.BrandingSettingsSaved, entry.Action);
    Assert.Contains(entry.Changes, change => change.Field == "Product name" && change.New == "Nova MSP");
}

[Theory]
[InlineData("PrimaryColour", "blue", "Primary colour must be a hex colour such as #1A73E8.")]
[InlineData("SupportEmail", "not-an-email", "Support email isn't a valid email address.")]
[InlineData("SupportUrl", "http://nova.example", "Support URL must be an absolute https:// address.")]
[InlineData("ProductName", "", "Product name can't be empty.")]
public async Task Save_RefusesInvalidValues(string field, string value, string message)
{
    await using var context = CreateContext();
    var updated = await BrandingSettingsService.GetAsync(context);
    typeof(BrandingSettings).GetProperty(field)!.SetValue(updated, value);

    var exception = await Assert.ThrowsAsync<ArgumentException>(() => BrandingSettingsService.SaveAsync(context, TestActors.Admin, updated));

    Assert.StartsWith(message, exception.Message);
}

[Fact]
public async Task ReplacingTheLogo_DeletesTheOldImage()
{
    await using var context = CreateContext();
    var first = await BrandingSettingsService.UploadImageAsync(context, TestActors.Admin, PngBytes());
    var settings = await BrandingSettingsService.GetAsync(context);
    settings.LogoImageId = first.ImageId;
    await BrandingSettingsService.SaveAsync(context, TestActors.Admin, settings);

    var second = await BrandingSettingsService.UploadImageAsync(context, TestActors.Admin, PngBytes());
    settings.LogoImageId = second.ImageId;
    await BrandingSettingsService.SaveAsync(context, TestActors.Admin, settings);

    await using var verify = CreateContext();
    Assert.Equal(second.ImageId, (await verify.BrandingImages.SingleAsync()).Id);
}

[Fact]
public async Task UploadingSomethingThatIsntALogo_SaysWhy_AndStoresNothing()
{
    await using var context = CreateContext();

    var upload = await BrandingSettingsService.UploadImageAsync(context, TestActors.Admin, "GIF89a"u8.ToArray());

    Assert.Equal((null, BrandingImages.WrongTypeOrSize), (upload.ImageId, upload.Problem));
    Assert.Empty(context.BrandingImages);
}

private static byte[] PngBytes() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
```

`BrandingLogoEndpointTests` (web, demo mode like `ClientPortalAccessTests`):

```csharp
[Fact]
public async Task ALogo_IsServedWithLongCaching_AndAnSvgIsLockedDown()
{
    Guid imageId;
    await using (var context = CreateContext())
    {
        var upload = await BrandingSettingsService.UploadImageAsync(context, TestActors.Admin, Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"));
        imageId = upload.ImageId!.Value;
    }

    using var client = _factory!.CreateClient();
    var response = await client.GetAsync($"/branding/logo/{imageId}");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("image/svg+xml", response.Content.Headers.ContentType!.MediaType);
    Assert.True(response.Headers.CacheControl!.Public);
    Assert.Equal(TimeSpan.FromDays(365), response.Headers.CacheControl.MaxAge);
    Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    Assert.Contains("default-src 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
}

[Fact]
public async Task AnUnknownLogo_IsNotFound()
{
    using var client = _factory!.CreateClient();

    Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/branding/logo/{Guid.NewGuid()}")).StatusCode);
}
```

(The upload in the first test happens after the factory has started, because demo startup resets the database; create the client first if the reset would wipe it, following `HaloWebhookEndpointTests`' comment about forcing the boot.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~BrandingSettingsServiceTests|FullyQualifiedName~BrandingLogoEndpointTests"`
Expected: build FAIL.

- [ ] **Step 3: Implement**

Entities as in Interfaces (class summaries per the spec). `DotMarcDbContext`: `DbSet<BrandingSettings> BrandingSettings`, `DbSet<BrandingImage> BrandingImages`; `BrandingImage.Id` is the key with `ValueGeneratedNever()`; `ContentType` max 40, `Sha256` max 64; `BrandingSettings` string lengths per the spec; `HasData(new BrandingSettings { Id = 1, ProductName = "dotMARC", PrimaryColour = BrandingSettings.DefaultPrimaryColour, SecondaryColour = BrandingSettings.DefaultSecondaryColour })`. Logo ids are plain nullable Guid columns (no FK), so deleting an image never cascades into settings.

```csharp
// src/DotMarc/Portal/BrandingSettingsService.cs
using System.Net.Mail;
using System.Security.Cryptography;
using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Portal;

public sealed record BrandingImageUpload(Guid? ImageId, string? Problem);

/// <summary>Reads and saves the MSP-wide brand the client portal uses, and stores uploaded logos.</summary>
public static class BrandingSettingsService
{
    public static Task<BrandingSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.BrandingSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, BrandingSettings updated, CancellationToken cancellationToken = default)
    {
        Normalise(updated);
        Validate(updated);

        var saved = await context.BrandingSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges()
            .Field("Product name", saved.ProductName, updated.ProductName)
            .Field("Primary colour", saved.PrimaryColour, updated.PrimaryColour)
            .Field("Secondary colour", saved.SecondaryColour, updated.SecondaryColour)
            .Field("Logo", saved.LogoImageId is null ? "None" : "Set", updated.LogoImageId is null ? "None" : (saved.LogoImageId == updated.LogoImageId ? "Set" : "Replaced"))
            .Field("Dark logo", saved.DarkLogoImageId is null ? "None" : "Set", updated.DarkLogoImageId is null ? "None" : (saved.DarkLogoImageId == updated.DarkLogoImageId ? "Set" : "Replaced"))
            .Field("Support email", saved.SupportEmail, updated.SupportEmail)
            .Field("Support URL", saved.SupportUrl, updated.SupportUrl)
            .Field("Support phone", saved.SupportPhone, updated.SupportPhone)
            .Field("Footer text", saved.FooterText, updated.FooterText);
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.BrandingSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(existing).CurrentValues.SetValues(updated);
        existing.Id = 1;
        AuditLog.Record(context, actor, AuditActions.BrandingSettingsSaved, AuditTarget.Settings("Branding"), "Saved branding", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BrandingImageCleanup.DeleteUnreferencedAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores a validated logo under a new id and returns it, or says why it can't be used. Storing isn't
    /// audited on its own: the logo only takes effect, and is audited, when branding using it is saved.</summary>
    public static async Task<BrandingImageUpload> UploadImageAsync(DotMarcDbContext context, AuditActor actor, byte[] bytes, CancellationToken cancellationToken = default)
    {
        var check = BrandingImages.Validate(bytes);
        if (check.ContentType is null)
        {
            return new BrandingImageUpload(null, check.Problem);
        }

        var image = new BrandingImage
        {
            Id = Guid.NewGuid(), ContentType = check.ContentType, Bytes = bytes,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), UploadedUtc = DateTimeOffset.UtcNow,
        };
        context.BrandingImages.Add(image);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new BrandingImageUpload(image.Id, null);
    }

    private static void Normalise(BrandingSettings settings)
    {
        settings.ProductName = settings.ProductName?.Trim() ?? "";
        settings.PrimaryColour = settings.PrimaryColour?.Trim().ToUpperInvariant() ?? "";
        settings.SecondaryColour = settings.SecondaryColour?.Trim().ToUpperInvariant() ?? "";
        settings.SupportEmail = Blank(settings.SupportEmail);
        settings.SupportUrl = Blank(settings.SupportUrl);
        settings.SupportPhone = Blank(settings.SupportPhone);
        settings.FooterText = Blank(settings.FooterText);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Validate(BrandingSettings settings)
    {
        if (settings.ProductName.Length == 0) throw new ArgumentException("Product name can't be empty.", nameof(settings));
        if (settings.ProductName.Length > 60) throw new ArgumentException("Product name can be at most 60 characters.", nameof(settings));
        if (!BrandColours.IsValid(settings.PrimaryColour)) throw new ArgumentException("Primary colour must be a hex colour such as #1A73E8.", nameof(settings));
        if (!BrandColours.IsValid(settings.SecondaryColour)) throw new ArgumentException("Secondary colour must be a hex colour such as #1A73E8.", nameof(settings));
        if (settings.SupportEmail is { } email && !MailAddress.TryCreate(email, out _)) throw new ArgumentException("Support email isn't a valid email address.", nameof(settings));
        if (settings.SupportUrl is { } url && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)) throw new ArgumentException("Support URL must be an absolute https:// address.", nameof(settings));
        if (settings.SupportPhone is { Length: > 40 }) throw new ArgumentException("Support phone can be at most 40 characters.", nameof(settings));
        if (settings.FooterText is { Length: > 200 }) throw new ArgumentException("Footer text can be at most 200 characters.", nameof(settings));
    }
}
```

`UploadImageAsync` doesn't start with Get/List/Count/Resolve and changes data, so it takes the actor (unused beyond satisfying the rule; keep it, since a later audit of uploads may want it). Add `typeof(BrandingSettingsService)` to `AuditCoverageTests`.

```csharp
// src/DotMarc/Portal/BrandingImageCleanup.cs
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Portal;

/// <summary>Removes logos nothing uses any more (replaced, cleared, or uploaded but never saved).</summary>
public static class BrandingImageCleanup
{
    public static async Task DeleteUnreferencedAsync(DotMarcDbContext context, CancellationToken cancellationToken)
    {
        var settings = await context.BrandingSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var inUse = new HashSet<Guid>(new[] { settings.LogoImageId, settings.DarkLogoImageId }.OfType<Guid>());
        foreach (var groupBranding in await context.GroupBrandings.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            inUse.UnionWith(new[] { groupBranding.LogoImageId, groupBranding.DarkLogoImageId }.OfType<Guid>());
        }

        // An upload made in the last hour may belong to a form still being filled in, so leave it for a later save.
        var recentCutoff = DateTimeOffset.UtcNow.AddHours(-1);
        await context.BrandingImages
            .Where(image => !inUse.Contains(image.Id) && image.UploadedUtc < recentCutoff)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

The `GroupBrandings` set arrives in Task 8. In this task, leave the `GroupBrandings` loop out and add it in Task 8 (the Task 8 brief says so). The replace test needs the old image deleted right away; since it was just uploaded, the one-hour grace would keep it. Ruling for the executor: delete unreferenced images that were referenced before this save immediately, plus older-than-an-hour orphans. Implement by passing the previously referenced ids into `DeleteUnreferencedAsync(context, IEnumerable<Guid> justReleased, ct)` and deleting `!inUse && (justReleased.Contains(id) || UploadedUtc < recentCutoff)`.

Logo endpoint in `Program.cs`, next to the other anonymous endpoints:

```csharp
// Logos are shown to clients, on sign-in pages and in emails, so they're served without sign-in. Each upload gets a new
// unguessable id, so the response can be cached for good.
app.MapGet("/branding/logo/{id:guid}", async (Guid id, HttpContext httpContext, IDbContextFactory<DotMarcDbContext> dbContextFactory) =>
{
    await using var context = await dbContextFactory.CreateDbContextAsync();
    var image = await context.BrandingImages.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id);
    if (image is null)
    {
        return Results.NotFound();
    }

    var headers = httpContext.Response.Headers;
    headers.CacheControl = "public, max-age=31536000, immutable";
    headers.ETag = $"\"{image.Sha256}\"";
    headers.XContentTypeOptions = "nosniff";
    if (image.ContentType == "image/svg+xml")
    {
        headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";
    }

    return Results.File(image.Bytes, image.ContentType);
}).AllowAnonymous();
```

`DemoDataSeeder`: add `"BrandingImages"` to the truncate list (not `BrandingSettings`, which is a seeded singleton; reset it in Task 8's seeding instead).

Run: `dotnet ef migrations add AddBranding --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj`.

- [ ] **Step 4: Run the tests and commit**

Run the Step 2 filter (PASS) and `dotnet test test/DotMarc.Tests`.

```bash
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Store the MSP brand and logos, and serve logos safely"
```

---

### Task 7: The portal wears the brand, and the Branding page

**Files:**
- Create: `src/DotMarc/Portal/PortalBranding.cs` (MSP-only resolution in this task), extend `src/DotMarc/Portal/PortalTheme.cs`
- Modify: `src/DotMarc/Components/Layout/PortalLayout.razor`
- Create: `src/DotMarc/Components/Pages/BrandingSettingsPage.razor`, `src/DotMarc/Components/Portal/PortalBrandPreview.razor`
- Modify: `src/DotMarc/Components/Layout/MainLayout.razor` (menu entry)
- Test: `test/DotMarc.Tests/Portal/PortalBrandingTests.cs`, `test/DotMarc.Tests/Portal/PortalThemeTests.cs`

**Interfaces:**
- Consumes: `BrandingSettings`, `BrandColours`.
- Produces:
  - `record ResolvedBrand(string ProductName, string Heading, string PrimaryColour, string SecondaryColour, Guid? LogoImageId, Guid? DarkLogoImageId, string? SupportEmail, string? SupportUrl, string? SupportPhone, string? FooterText)`
  - `PortalBranding.Resolve(BrandingSettings msp, IReadOnlyList<ScopedGroupBrand> scopedGroups) : ResolvedBrand`, `record ScopedGroupBrand(string GroupName, GroupBranding? Branding)`. In this task `GroupBranding` doesn't exist yet: define `ScopedGroupBrand(string GroupName)` now and add the `Branding` parameter in Task 8 (Task 8's brief says so).
  - `PortalTheme.For(ResolvedBrand brand) : MudTheme`
  - `PortalBrandLoader(IDbContextFactory<DotMarcDbContext>)` scoped, `Task<ResolvedBrand> LoadAsync(IReadOnlyCollection<int> groupIds, CancellationToken ct = default)`
  - route `/branding/settings`, policy `AccessManage`

- [ ] **Step 1: Write the failing tests**

```csharp
public sealed class PortalBrandingTests
{
    private static BrandingSettings Msp() => new()
    {
        ProductName = "Nova MSP", PrimaryColour = "#0B5FFF", SecondaryColour = "#FF6B00",
        LogoImageId = Guid.Parse("11111111-1111-1111-1111-111111111111"), SupportEmail = "help@nova-msp.example",
    };

    [Fact]
    public void OneScopedGroup_HeadsThePageWithItsName_InTheMspBrand()
    {
        var brand = PortalBranding.Resolve(Msp(), [new ScopedGroupBrand("Aurora Retail")]);

        Assert.Equal(("Nova MSP", "Aurora Retail", "#0B5FFF", "help@nova-msp.example"), (brand.ProductName, brand.Heading, brand.PrimaryColour, brand.SupportEmail));
    }

    [Fact]
    public void SeveralScopedGroups_AreHeadedWithTheProductName()
    {
        var brand = PortalBranding.Resolve(Msp(), [new ScopedGroupBrand("Aurora Retail"), new ScopedGroupBrand("Aurora Online")]);

        Assert.Equal("Nova MSP", brand.Heading);
    }

    [Fact]
    public void TheDarkLogo_FallsBackToTheLightOne()
    {
        var brand = PortalBranding.Resolve(Msp(), [new ScopedGroupBrand("Aurora Retail")]);

        Assert.Equal(brand.LogoImageId, brand.DarkLogoImageId);
    }
}

public sealed class PortalThemeTests
{
    [Fact]
    public void TheBrandColours_AreThePortalsPrimaryAndSecondary_InLightAndDark()
    {
        var theme = PortalTheme.For(new ResolvedBrand("Nova MSP", "Aurora Retail", "#0B5FFF", "#FF6B00", null, null, null, null, null, null));

        Assert.Equal("#0b5fffff", theme.PaletteLight.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.HexA).ToLowerInvariant());
        Assert.Equal("#ff6b00ff", theme.PaletteDark.Secondary.ToString(MudBlazor.Utilities.MudColorOutputFormats.HexA).ToLowerInvariant());
    }
}
```

(Check the `MudColor` formatting API in MudBlazor 9.8; if `ToString(MudColorOutputFormats.HexA)` isn't it, compare `.Value` or use the API the existing code uses.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PortalBrandingTests|FullyQualifiedName~PortalThemeTests"`
Expected: build FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/DotMarc/Portal/PortalBranding.cs
namespace DotMarc.Portal;

public sealed record ResolvedBrand(string ProductName, string Heading, string PrimaryColour, string SecondaryColour,
    Guid? LogoImageId, Guid? DarkLogoImageId, string? SupportEmail, string? SupportUrl, string? SupportPhone, string? FooterText);

public sealed record ScopedGroupBrand(string GroupName);

/// <summary>Works out the brand a portal user sees from the MSP default and their scoped Groups.</summary>
public static class PortalBranding
{
    public static ResolvedBrand Resolve(BrandingSettings msp, IReadOnlyList<ScopedGroupBrand> scopedGroups)
    {
        var heading = scopedGroups.Count == 1 ? scopedGroups[0].GroupName : msp.ProductName;
        return new ResolvedBrand(msp.ProductName, heading, msp.PrimaryColour, msp.SecondaryColour,
            msp.LogoImageId, msp.DarkLogoImageId ?? msp.LogoImageId, msp.SupportEmail, msp.SupportUrl, msp.SupportPhone, msp.FooterText);
    }
}
```

`PortalTheme.For(brand)`: start from a copy of `PortalTheme.Default` (build a new `MudTheme` with the same palettes, typography and layout properties as Default) and set `PaletteLight.Primary`, `PaletteLight.Secondary`, `PaletteDark.Primary`, `PaletteDark.Secondary` and `PaletteLight.AppbarBackground` to the brand colours (dark keeps its own app bar background).

`PortalBrandLoader`:

```csharp
public sealed class PortalBrandLoader(IDbContextFactory<DotMarcDbContext> dbFactory)
{
    public async Task<ResolvedBrand> LoadAsync(IReadOnlyCollection<int> groupIds, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var msp = await BrandingSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        var groups = await context.Groups.AsNoTracking().Where(group => groupIds.Contains(group.Id))
            .Select(group => new ScopedGroupBrand(group.Name)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return PortalBranding.Resolve(msp, groups);
    }
}
```

Register it scoped.

`PortalLayout`: in `OnInitializedAsync` load the brand for the user's `PortalData.ScopedGroupIds`; the theme is `PortalTheme.For(brand)`; the app bar shows `<img src="/branding/logo/{id}" alt="@brand.ProductName" height="40" />` (dark logo in dark mode) or the product name as text when there's no logo; the heading text (`brand.Heading`) beside it; the `<PageTitle>` suffix and the footer show the support email (mailto link), URL, phone and footer text when set. While loading, render the default theme so nothing flashes unstyled.

`BrandingSettingsPage.razor` (`@page "/branding/settings"`, `[Authorize(Policy = "AccessManage")]`, MainLayout): a form bound to a working copy of `BrandingSettings` with `FieldWithHelp` on every field:
- Product name.
- Primary colour and Secondary colour: a `MudColorPicker` (hex, no alpha) plus the hex text; beside each, a `MudAlert` warning when `BrandColours.ContrastRatio(colour, "#FFFFFF") < BrandColours.MinimumTextContrast` ("This colour is hard to read on white. Text in it may be too faint for some people.").
- Logo and Dark logo: `InputFile` (accept `image/png,image/jpeg,image/svg+xml`, max 512 KB read via `OpenReadStream(BrandingImages.MaximumBytes)`), calling `UploadImageAsync`; a refused upload shows its `Problem` as an error snackbar; a thumbnail from `/branding/logo/{id}`; a Remove button setting the id to null.
- Support email, URL, phone, footer text.
- `<PortalBrandPreview Brand="..." />` showing the app bar and one sample domain card (a fake `PortalDomainSummary` for "example.com", Protected) in light and dark side by side, each wrapped in its own `MudThemeProvider`-free container using inline CSS variables `--mud-palette-primary` and `--mud-palette-secondary` set to the brand colours (a nested theme provider would change the whole page).
- Save calls `BrandingSettingsService.SaveAsync`; an `ArgumentException` shows its message.

`MainLayout.razor`: inside the `AccessManage` menu block add `<MudMenuItem Href="/branding/settings" Icon="@Icons.Material.Filled.Palette">Branding</MudMenuItem>`.

- [ ] **Step 4: Run, browser check, commit**

Run the Step 2 filter and `dotnet test test/DotMarc.Tests` (all pass). Browser check: as Demo Admin open Branding, change the product name and colours, upload an SVG and a PNG logo, check the preview and the contrast warning, save; as Demo Client the portal shows the new brand in light and dark. Stop only your own process.

```bash
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Brand the client portal and add the Branding settings page"
```

---

## Phase 3: Group overrides

### Task 8: Group branding and its resolution

**Files:**
- Create: `src/DotMarc/Portal/GroupBranding.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs`, `src/DotMarc/Data/GroupManagementService.cs`, `src/DotMarc/Audit/AuditActions.cs`, `src/DotMarc/Portal/PortalBranding.cs`, `src/DotMarc/Portal/PortalBrandLoader.cs` (or wherever it lives), `src/DotMarc/Portal/BrandingImageCleanup.cs`, `src/DotMarc/Demo/DemoDataSeeder.cs`
- Create: migration `AddGroupBranding`
- Test: extend `PortalBrandingTests.cs`, `GroupManagementServiceTests.cs`, `BrandingSettingsServiceTests.cs`

**Interfaces:**
- Consumes: Task 7's `PortalBranding`, `ScopedGroupBrand`, `ResolvedBrand`; Task 6's `BrandingImageCleanup`.
- Produces:
  - `GroupBranding { int GroupId; string? DisplayName; Guid? LogoImageId; Guid? DarkLogoImageId; string? PrimaryColour; string? SecondaryColour }`
  - `ScopedGroupBrand(string GroupName, GroupBranding? Branding)` (the second parameter added here)
  - `record GroupBrandingInput(string? DisplayName, Guid? LogoImageId, Guid? DarkLogoImageId, string? PrimaryColour, string? SecondaryColour)`
  - `GroupManagementService.SetBrandingAsync(DotMarcDbContext context, AuditActor actor, int groupId, GroupBrandingInput input, CancellationToken ct = default)`
  - `GroupManagementService.GetBrandingAsync(DotMarcDbContext context, int groupId, CancellationToken ct = default) : Task<GroupBranding?>`
  - `AuditActions.GroupBrandingChanged = "group.branding_changed"` ("Group branding changed")

- [ ] **Step 1: Write the failing tests**

Add to `PortalBrandingTests` (update the existing ones to `new ScopedGroupBrand(name, null)`):

```csharp
[Fact]
public void OneBrandedGroup_OverridesTheFieldsItSets_AndKeepsTheRest()
{
    var aurora = new GroupBranding { DisplayName = "Aurora Retail Ltd", PrimaryColour = "#7A1FA2" };

    var brand = PortalBranding.Resolve(Msp(), [new ScopedGroupBrand("Aurora Retail", aurora)]);

    Assert.Equal(("Aurora Retail Ltd", "#7A1FA2", "#FF6B00", "Nova MSP"), (brand.Heading, brand.PrimaryColour, brand.SecondaryColour, brand.ProductName));
    Assert.Equal(Msp().LogoImageId, brand.LogoImageId);
}

[Fact]
public void TwoBrandedGroups_UseTheMspBrand_AndTheProductName()
{
    var brand = PortalBranding.Resolve(Msp(),
    [
        new ScopedGroupBrand("Aurora Retail", new GroupBranding { PrimaryColour = "#7A1FA2" }),
        new ScopedGroupBrand("Aurora Online", new GroupBranding { PrimaryColour = "#00897B" }),
    ]);

    Assert.Equal(("Nova MSP", "#0B5FFF"), (brand.Heading, brand.PrimaryColour));
}

[Fact]
public void OneBrandedGroupAmongSeveral_IsUsed_ButTheHeadingIsTheProductName()
{
    var brand = PortalBranding.Resolve(Msp(),
    [
        new ScopedGroupBrand("Aurora Retail", new GroupBranding { PrimaryColour = "#7A1FA2" }),
        new ScopedGroupBrand("Aurora Online", null),
    ]);

    Assert.Equal(("Nova MSP", "#7A1FA2"), (brand.Heading, brand.PrimaryColour));
}

[Fact]
public void AGroupLogo_WithoutADarkOne_IsUsedInDarkModeToo()
{
    var logo = Guid.NewGuid();

    var brand = PortalBranding.Resolve(Msp(), [new ScopedGroupBrand("Aurora Retail", new GroupBranding { LogoImageId = logo })]);

    Assert.Equal((logo, logo), (brand.LogoImageId, brand.DarkLogoImageId));
}
```

`GroupManagementServiceTests`:

```csharp
[Fact]
public async Task SetBrandingAsync_SavesAndAudits_AndClearingEveryFieldRemovesTheRow()
{
    await using var context = CreateContext();
    var group = new Group { Name = "Aurora Retail" };
    context.Groups.Add(group);
    await context.SaveChangesAsync();

    await GroupManagementService.SetBrandingAsync(context, TestActors.Admin, group.Id, new GroupBrandingInput("Aurora Retail Ltd", null, null, "#7A1FA2", null));
    await using (var verify = CreateContext())
    {
        var saved = await GroupManagementService.GetBrandingAsync(verify, group.Id);
        Assert.Equal(("Aurora Retail Ltd", "#7A1FA2"), (saved!.DisplayName, saved.PrimaryColour));
        Assert.Equal(AuditActions.GroupBrandingChanged, (await verify.AuditEntries.SingleAsync()).Action);
    }

    await GroupManagementService.SetBrandingAsync(context, TestActors.Admin, group.Id, new GroupBrandingInput(null, null, null, null, null));

    await using var verifyCleared = CreateContext();
    Assert.Null(await GroupManagementService.GetBrandingAsync(verifyCleared, group.Id));
}

[Fact]
public async Task SetBrandingAsync_RefusesAnInvalidColour()
{
    await using var context = CreateContext();
    var group = new Group { Name = "Aurora Retail" };
    context.Groups.Add(group);
    await context.SaveChangesAsync();

    var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
        GroupManagementService.SetBrandingAsync(context, TestActors.Admin, group.Id, new GroupBrandingInput(null, null, null, "purple", null)));

    Assert.StartsWith("Primary colour must be a hex colour such as #1A73E8.", exception.Message);
}

[Fact]
public async Task DeletingAGroup_DeletesItsBranding()
{
    await using var context = CreateContext();
    var group = new Group { Name = "Aurora Retail" };
    context.Groups.Add(group);
    await context.SaveChangesAsync();
    await GroupManagementService.SetBrandingAsync(context, TestActors.Admin, group.Id, new GroupBrandingInput("Aurora Retail Ltd", null, null, null, null));

    await GroupManagementService.RemoveGroupAsync(context, TestActors.Admin, group.Id);

    await using var verify = CreateContext();
    Assert.Empty(verify.GroupBrandings);
}
```

`BrandingSettingsServiceTests`: add a test that a logo used by a Group's branding survives an MSP save that releases a different logo, so cleanup counts Group logos as in use.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PortalBrandingTests|FullyQualifiedName~GroupManagementServiceTests|FullyQualifiedName~BrandingSettingsServiceTests"`
Expected: build FAIL.

- [ ] **Step 3: Implement**

`GroupBranding` entity, `DbSet<GroupBranding> GroupBrandings`, key `GroupId`, `HasOne<Group>().WithOne().HasForeignKey<GroupBranding>(branding => branding.GroupId).OnDelete(DeleteBehavior.Cascade)`, lengths per the spec. Migration `AddGroupBranding`.

`ScopedGroupBrand(string GroupName, GroupBranding? Branding)`; `PortalBranding.Resolve`:

```csharp
public static ResolvedBrand Resolve(BrandingSettings msp, IReadOnlyList<ScopedGroupBrand> scopedGroups)
{
    var branded = scopedGroups.Where(group => group.Branding is not null).ToList();
    var groupBrand = branded.Count == 1 ? branded[0].Branding : null;
    var heading = scopedGroups.Count == 1 ? scopedGroups[0].Branding?.DisplayName ?? scopedGroups[0].GroupName : msp.ProductName;

    var logo = groupBrand?.LogoImageId ?? msp.LogoImageId;
    var darkLogo = groupBrand?.LogoImageId is not null
        ? groupBrand.DarkLogoImageId ?? groupBrand.LogoImageId
        : groupBrand?.DarkLogoImageId ?? msp.DarkLogoImageId ?? msp.LogoImageId;

    return new ResolvedBrand(msp.ProductName, heading,
        groupBrand?.PrimaryColour ?? msp.PrimaryColour, groupBrand?.SecondaryColour ?? msp.SecondaryColour,
        logo, darkLogo, msp.SupportEmail, msp.SupportUrl, msp.SupportPhone, msp.FooterText);
}
```

(A Group's own light logo pairs with its own dark logo, never with the MSP's dark logo.)

`PortalBrandLoader` selects `new ScopedGroupBrand(group.Name, context.GroupBrandings.FirstOrDefault(branding => branding.GroupId == group.Id))` (or a join).

`GroupManagementService.SetBrandingAsync`: trim and blank-to-null each field; validate colours with `BrandColours.IsValid` (same messages as the MSP service); `DisplayName` max 100; load the existing row; build `AuditChanges` (Display name, Logo set/none/replaced, Dark logo, Primary colour, Secondary colour); return early if nothing changed; if every field is null remove the row, otherwise upsert; `AuditLog.Record(..., AuditActions.GroupBrandingChanged, AuditTarget.For(group), $"Changed the portal branding for group {group.Name}", changes)`; save; then `BrandingImageCleanup.DeleteUnreferencedAsync(context, released, ct)` with the logo ids this change released. `GetBrandingAsync` reads the row.

`BrandingImageCleanup`: add the `GroupBrandings` loop shown in Task 6.

`DemoDataSeeder`: add `"GroupBrandings"` to the truncate list; after seeding, update `BrandingSettings` row 1 to `ProductName = "Nova MSP"`, `PrimaryColour = "#0B5FFF"`, `SecondaryColour = "#FF6B00"`, `SupportEmail = "help@nova-msp.example"`, `SupportUrl = "https://nova-msp.example/support"`, logos null, and add `new GroupBranding { GroupId = groupsByName[ViewerScopedGroupName].Id, DisplayName = "Aurora Retail Ltd", PrimaryColour = "#7A1FA2" }` (save the groups first so their ids exist, following the seeder's existing save order).

- [ ] **Step 4: Run the tests and commit**

Run the Step 2 filter (PASS) and `dotnet test test/DotMarc.Tests`.

```bash
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Let a Group override the portal branding"
```

---

### Task 9: Group branding dialog, preview as client, docs and roadmap

**Files:**
- Create: `src/DotMarc/Components/Dialogs/GroupBrandingDialog.razor`
- Create: `src/DotMarc/Components/Pages/Portal/PortalPreview.razor`
- Modify: `src/DotMarc/Components/Pages/ManageGroups.razor`, `src/DotMarc/Components/Layout/PortalLayout.razor` (accept an explicit group set for preview)
- Create: `website/docs/client-portal.mdx`; modify `website/sidebars.ts`, `website/scripts/canny-roadmap.json`, `website/docs/permissions-and-access.mdx`
- Test: extend `ClientPortalAccessTests.cs`

**Interfaces:**
- Consumes: `GroupManagementService.SetBrandingAsync/GetBrandingAsync`, `BrandingSettingsService.UploadImageAsync`, `PortalData`, `PortalBrandLoader`.
- Produces: route `/portal/preview/{groupId:int}` (policy `GroupsOrTagsWrite`), `PortalLayout`'s preview mode via a cascading `PortalScope` value: `record PortalScope(IReadOnlyCollection<int> GroupIds, string? PreviewOf)`.

- [ ] **Step 1: Write the failing web tests**

```csharp
[Fact]
public async Task Staff_CanPreviewAGroupsPortal()
{
    using var client = await SignInAsync("admin");
    var auroraGroupId = await GroupIdAsync(DotMarc.Demo.DemoDataSeeder.ViewerScopedGroupName);

    var html = await client.GetStringAsync($"/portal/preview/{auroraGroupId}");

    Assert.Contains("Preview: this is what clients of Aurora Retail see", html);
    Assert.Contains("Aurora Retail Ltd", html);
}

[Fact]
public async Task APortalUser_CantUseThePreview()
{
    using var client = await SignInAsync("client");

    var response = await client.GetAsync("/portal/preview/1");

    Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    Assert.StartsWith("/portal", response.Headers.Location!.PathAndQuery);
    Assert.DoesNotContain("preview", response.Headers.Location!.PathAndQuery);
}
```

`GroupIdAsync(name)` reads the group's id from the test database through a context.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ClientPortalAccessTests"`
Expected: FAIL (preview route missing).

- [ ] **Step 3: Implement**

`PortalScope`: `PortalHome`, `PortalDomain` and `PortalLayout` read the group ids from a `[CascadingParameter] PortalScope?`; when absent they use `PortalData.ScopedGroupIds(user)`. `PortalPreview.razor` (`@page "/portal/preview/{GroupId:int}"`, `@layout PortalLayout`, `[Authorize(Policy = "GroupsOrTagsWrite")]`) wraps `<PortalHome />` in `<CascadingValue Value="new PortalScope([GroupId], groupName)">`. Because the layout is outside the page's cascade, give `PortalLayout` a way to learn the preview scope: the preview page sets it through a scoped `PortalPreviewState` service (`GroupIds`, `PreviewOf`) that the layout reads in `OnParametersSet` and falls back to the user's claims when unset. Domain links inside a preview point to `/portal/preview/{GroupId}/domains/{name}` (add that route to `PortalPreview` too, rendering `PortalDomain`). The layout shows a `MudAlert Severity="Severity.Info"` banner "Preview: this is what clients of {PreviewOf} see" with a "Back to Manage groups" link.

`GroupBrandingDialog.razor` (`[Parameter] int GroupId`, `[Parameter] string GroupName`): loads `GetBrandingAsync` and `BrandingSettingsService.GetAsync`; fields Display name, Logo, Dark logo (upload as on the Branding page), Primary and Secondary colour, each with a "Using the MSP default ({value})" helper when blank and a Clear button; `<PortalBrandPreview>` with `PortalBranding.Resolve(msp, [new ScopedGroupBrand(GroupName, unsavedBranding)])`; Save calls `SetBrandingAsync`; a **Preview as client** link to `/portal/preview/{GroupId}` (opens in a new tab).

`ManageGroups.razor`: a **Branding** button in each row (inside `AuthorizeView Policy="GroupsOrTagsWrite"`) opening the dialog, with a small "Branded" chip when the Group has a `GroupBranding` row (load the set of branded group ids with the groups).

Docs: `website/docs/client-portal.mdx` (`sidebar_position` after permissions-and-access): what the portal is; turning it on for a grant (must be limited to Groups; the Access page switch); what clients see (home and domain page; statuses explained: Protected, Monitoring only, Needs attention, No reports yet); branding (Manage > Branding fields, logos and their rules, contrast warning; Group overrides and how several Groups resolve); preview as client; what clients can't do. Link it from `permissions-and-access.mdx` next to scoped access, and add `'client-portal'` to the sidebar's administer section. Roadmap: mark "Add a branded client portal view" complete with details describing what shipped.

- [ ] **Step 4: Run, browser check, docs build, commit**

Run the Step 2 filter and `dotnet test test/DotMarc.Tests` (all pass). `cd website && npx docusaurus build` (no broken links). Browser check (demo): Demo Client sees "Aurora Retail Ltd" in purple with the Nova MSP support footer, light and dark; Demo Admin edits Aurora Retail's branding in the dialog, previews as client, and the preview banner shows. Stop only your own process.

```bash
git add -A src/DotMarc test/DotMarc.Tests website
git commit -m "Add Group branding, preview as client, and client portal docs"
```
