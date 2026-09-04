using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.DnsPush;
using DotMarc.Graph;
using DotMarc.Ingestion;
using DotMarc.MtaSts;
using DotMarc.Notifications;
using MudBlazor.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

// The container listens on plain HTTP behind a TLS-terminating reverse proxy (see README) or,
// when deployed to Azure, behind Container Apps' ingress proxy. ASP.NET Core otherwise
// builds the OIDC redirect_uri from the request's own scheme, which is http unless forwarded
// headers are processed — sending http://host/signin-oidc to Entra when https://host/signin-oidc
// is what's registered, breaking sign-in with AADSTS50011.
//
// KnownProxies/KnownIPNetworks default to trusting only loopback, which Container Apps'
// ingress proxy never is (it's never on loopback from the container's perspective, and a
// self-hosted reverse proxy may not be either). The container has no other ingress path in
// either supported deployment model — it is never directly reachable except through that
// trusted front-end — so clearing both restrictions to trust any upstream proxy is safe here.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

var connectionString = builder.Configuration.GetConnectionString("DotMarc") ?? "Host=localhost;Database=dotmarc;Username=dotmarc;Password=dotmarc";

// AddDbContextFactory registers DotMarcDbContext itself as a scoped service too (in addition to
// the singleton IDbContextFactory<DotMarcDbContext>), so this one call covers both consumers:
// PollingService's existing IServiceScopeFactory-based scoped resolution (see the
// [ActivatorUtilitiesConstructor] host constructor below), and Dashboard.razor/DomainDetail.razor,
// which use the factory directly to create a short-lived context per render instead of holding one
// scoped/tracked context for the whole Blazor Server circuit. Do NOT also call AddDbContext here —
// combined with AddDbContextFactory it creates a scoped/singleton DbContextOptions<T> conflict that
// only surfaces when ASP.NET Core's DI container validates scopes, i.e. in Development
// (WebApplication.CreateBuilder enables ValidateScopes/ValidateOnBuild there): builder.Build()
// throws "Cannot consume scoped service 'DbContextOptions<DotMarcDbContext>' from singleton
// 'IDbContextFactory<DotMarcDbContext>'". Production skips that validation, which is why this
// wasn't caught by a Docker smoke test alone.
builder.Services.AddDbContextFactory<DotMarcDbContext>(options => options.UseNpgsql(connectionString));

// Previously unconfigured — Data Protection fell back to its default (non-durable across
// restarts/redeploys/replicas) key store, which DnsPushStateProtector tolerated only because its
// state is minutes-lived. Secrets stored via DatabaseSecretStore need real durability, the same
// argument that already moved NotificationSettings into Postgres.
builder.Services.AddDataProtection().PersistKeysToDbContext<DotMarcDbContext>();

// DemoDataResetService and the Razor components resolve IOptions<DemoOptions> from the
// container, validated (ResetHourUtc must be 0-23) and checked on start here, same pattern as
// GraphOptions below. The plain local `demoOptions` variable further down is a separate,
// unvalidated read of the same section: it has to stay a plain bind because it's read
// synchronously here, before builder.Build() runs any startup validation, to make early
// branching decisions (skip GraphOptions, register the demo auth scheme, etc.).
builder.Services.AddOptions<DotMarc.Demo.DemoOptions>()
    .Bind(builder.Configuration.GetSection(DotMarc.Demo.DemoOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var demoOptions = new DotMarc.Demo.DemoOptions();
builder.Configuration.GetSection(DotMarc.Demo.DemoOptions.SectionName).Bind(demoOptions);

builder.Services.AddSingleton<IPsaTicketService, PsaTicketService>();
builder.Services.AddSingleton<IAlertingService, AlertingService>();

if (!demoOptions.Enabled)
{
    builder.Services.AddOptions<GraphOptions>()
        .Bind(builder.Configuration.GetSection(GraphOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services.AddSingleton<IGraphTokenProvider, ConfidentialClientGraphTokenProvider>();

    builder.Services.AddHttpClient<IGraphMailboxClient, GraphMailboxClient>(client =>
    {
        client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
    });

    builder.Services.AddHttpClient<ITlsrptGraphMailboxClient, TlsrptGraphMailboxClient>(client =>
    {
        client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
    });
}

builder.Services.AddHttpClient<IDmarcDnsChecker, DmarcDnsChecker>(client =>
{
    client.BaseAddress = new Uri("https://cloudflare-dns.com/");
    client.DefaultRequestHeaders.Add("Accept", "application/dns-json");
});

builder.Services.AddHttpClient<ITlsrptDnsChecker, TlsrptDnsChecker>(client =>
{
    client.BaseAddress = new Uri("https://cloudflare-dns.com/");
    client.DefaultRequestHeaders.Add("Accept", "application/dns-json");
});

// MTA-STS hosting is opt-in per deployment (see MtaStsOptions), so this section is intentionally
// not validated at startup the way GraphOptions is above — a deployment that never sets
// MtaSts:HostingHostname simply never enables MtaStsEnabled on any domain, and the background
// cycle no-ops without it (see PollingService.RunMtaStsCheckCycleAsync).
builder.Services.Configure<DotMarc.MtaSts.MtaStsOptions>(builder.Configuration.GetSection(DotMarc.MtaSts.MtaStsOptions.SectionName));

builder.Services.AddHttpClient<ITeamsWebhookClient, TeamsWebhookClient>();
builder.Services.AddHttpClient<IGenericWebhookClient, GenericWebhookClient>();
builder.Services.AddSingleton<IAlertWebhookClient, AlertWebhookClient>();

// KeyVault:VaultUri is only set by infra/main.bicep when enableKeyVaultWrite is true (see
// KeyVault__VaultUri there); every other deployment — including local/Docker Compose — leaves it
// unset and falls back to the Postgres-backed store.
var keyVaultUri = builder.Configuration["KeyVault:VaultUri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Services.AddSingleton(new SecretClient(new Uri(keyVaultUri), new DefaultAzureCredential()));
    builder.Services.AddSingleton<ISecretStore, KeyVaultSecretStore>();
}
else
{
    builder.Services.AddSingleton<ISecretStore, DatabaseSecretStore>();
}

builder.Services.AddSingleton<HaloPsaTokenCache>();
builder.Services.AddHttpClient<IHaloPsaClient, HaloPsaClient>();

// Runs regardless of demo mode: it only reads Domain rows already in the database (no Graph
// mailbox dependency), so it's just as meaningful against seeded demo data as against real
// polled reports.
builder.Services.AddHostedService<PinnedDomainHealthMonitor>();

builder.Services.AddHttpClient<DotMarc.MtaSts.IMtaStsDnsVerifier, DotMarc.MtaSts.MtaStsDnsVerifier>(client =>
{
    client.BaseAddress = new Uri("https://cloudflare-dns.com/");
    client.DefaultRequestHeaders.Add("Accept", "application/dns-json");
});

builder.Services.AddHttpClient<DotMarc.MtaSts.IMxHostsLookup, DotMarc.MtaSts.MxHostsLookup>(client =>
{
    client.BaseAddress = new Uri("https://cloudflare-dns.com/");
    client.DefaultRequestHeaders.Add("Accept", "application/dns-json");
});

// No fixed BaseAddress: unlike the typed clients above, this one requests a different hostname
// (mta-sts.<domain>) per call, so each request carries its own absolute URI.
builder.Services.AddHttpClient<DotMarc.MtaSts.IMtaStsServingVerifier, DotMarc.MtaSts.MtaStsServingVerifier>();

builder.Services.AddScoped<DotMarc.MtaSts.IMtaStsHostProvisioner>(sp =>
{
    var mtaStsOptions = sp.GetRequiredService<IOptions<DotMarc.MtaSts.MtaStsOptions>>();
    return string.Equals(mtaStsOptions.Value.Provisioner, "Azure", StringComparison.OrdinalIgnoreCase)
        ? new DotMarc.MtaSts.AzureMtaStsHostProvisioner(mtaStsOptions, sp.GetRequiredService<ILogger<DotMarc.MtaSts.AzureMtaStsHostProvisioner>>())
        : new DotMarc.MtaSts.CaddyMtaStsHostProvisioner();
});

builder.Services.AddHttpClient<DotMarc.DnsPush.CloudflareDnsPushProvider>();
builder.Services.AddSingleton<DotMarc.DnsPush.IDnsPushProvider>(sp => sp.GetRequiredService<DotMarc.DnsPush.CloudflareDnsPushProvider>());

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

builder.Services.AddHttpClient<DotMarc.DnsPush.ITlsrptTxtLookup, DotMarc.DnsPush.TlsrptTxtLookup>(client =>
{
    client.BaseAddress = new Uri("https://cloudflare-dns.com/");
    client.DefaultRequestHeaders.Add("Accept", "application/dns-json");
});

builder.Services.AddHttpClient<DotMarc.MtaSts.IMtaStsCnameLookup, DotMarc.MtaSts.MtaStsCnameLookup>(client =>
{
    client.BaseAddress = new Uri("https://cloudflare-dns.com/");
    client.DefaultRequestHeaders.Add("Accept", "application/dns-json");
});

builder.Services.AddSingleton<DotMarc.DnsPush.DnsPushStateProtector>();

builder.Services.AddHttpClient<DotMarc.IpEnrichment.IIpInfoLookup, DotMarc.IpEnrichment.RdapIpInfoLookup>(client =>
{
    client.BaseAddress = new Uri("https://rdap.org/");
    client.DefaultRequestHeaders.Add("User-Agent", "dotMARC (+https://github.com/homotechsual/dotMARC)");
    // Only 4 lookups can run concurrently app-wide (DomainDetail.razor's EnrichmentThrottle
    // semaphore), so HttpClient's 100s default timeout would let one hung request block that
    // permit long enough to stall enrichment for every other visitor. 10s is generous for a
    // simple RDAP GET.
    client.Timeout = TimeSpan.FromSeconds(10);
});

if (demoOptions.Enabled)
{
    builder.Services.AddHostedService<DotMarc.Demo.DemoDataResetService>();
}
else
{
    // PollingService has two constructors (one for direct test construction, one for the real
    // DI-scoped host path), both with 3 parameters. The built-in container's own constructor
    // selection does NOT consult [ActivatorUtilitiesConstructor] when activating a plain
    // AddHostedService<PollingService>() registration, so that alone throws "ambiguous
    // constructors" here (both IGraphMailboxClient and DotMarcDbContext are also registered in
    // this container). Routing activation through ActivatorUtilities.CreateInstance explicitly
    // does honor that attribute, so it deterministically selects the host constructor.
    builder.Services.AddHostedService<PollingService>(sp => ActivatorUtilities.CreateInstance<PollingService>(sp));
}

if (demoOptions.Enabled)
{
    builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.LoginPath = "/demo";
            options.AccessDeniedPath = "/AccessDenied";
        });
}
else
{
    builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("EntraId"));

    // AddMicrosoftIdentityWebApp wires up cookie authentication under CookieAuthenticationDefaults's
    // standard "Cookies" scheme alongside OpenIdConnect. Its default AccessDeniedPath sends a denied
    // user to a generic ASP.NET Core 403 page; pointing it at our own AccessDenied.razor instead gives
    // them an explanation instead of a raw 404/403.
    builder.Services.Configure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(
        Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
        options => options.AccessDeniedPath = "/AccessDenied");
}

builder.Services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation, DotMarc.Security.UserAccessClaimsTransformation>();

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim(DotMarc.Security.UserAccessClaimsTransformation.PermissionClaimType)
        .Build();

    foreach (var permission in Enum.GetValues<Permission>())
    {
        options.AddPolicy(permission.ToString(), policy =>
            policy.RequireClaim(DotMarc.Security.UserAccessClaimsTransformation.PermissionClaimType, permission.ToString()));
    }

    options.AddPolicy("DomainsWrite", policy => policy.RequireClaim(
        DotMarc.Security.UserAccessClaimsTransformation.PermissionClaimType,
        nameof(Permission.DomainsAdd), nameof(Permission.DomainsEdit), nameof(Permission.DomainsReorder), nameof(Permission.DomainsDelete)));

    options.AddPolicy("GroupsOrTagsWrite", policy => policy.RequireClaim(
        DotMarc.Security.UserAccessClaimsTransformation.PermissionClaimType,
        nameof(Permission.GroupsAdd), nameof(Permission.GroupsRename), nameof(Permission.GroupsDelete),
        nameof(Permission.TagsAdd), nameof(Permission.TagsEdit), nameof(Permission.TagsDelete)));
});

builder.Services.Configure<InitialAdminsOptions>(builder.Configuration.GetSection(InitialAdminsOptions.SectionName));

builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<DotMarcDbContext>();
    await DatabaseMigrator.MigrateWithLeaderLockAsync(context);
    // AccessBootstrapper is a static class (matching this project's other *ManagementService
    // statics), so it can't take a constructor-injected ILogger<AccessBootstrapper> the way
    // PollingService does — a static class can't be used as a generic type argument. Creating a
    // logger from the category type directly gets the same category-name behavior ILogger<T>
    // would have given a non-static class.
    var accessBootstrapperLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AccessBootstrapper));
    await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, scope.ServiceProvider.GetRequiredService<IOptions<InitialAdminsOptions>>(), accessBootstrapperLogger);

    if (demoOptions.Enabled)
    {
        // AccessBootstrapper (just above) already saved and tracks Admin/Viewer Role entities on
        // this same context. DemoDataSeeder.ResetAsync truncates every table with raw SQL — which
        // bypasses the change tracker entirely — then inserts its own fresh Role rows; Postgres
        // reissues identity 1 for those (RESTART IDENTITY), colliding with the still-tracked stale
        // Role from bootstrap. Clearing the tracker first (nothing above still needs saving; it's
        // all already persisted) avoids that collision.
        context.ChangeTracker.Clear();

        var nowUtc = DateTimeOffset.UtcNow;
        var dataset = DotMarc.Demo.DemoDataGenerator.Generate(new Random(DotMarc.Demo.DemoDataResetService.SeedFor(nowUtc)), nowUtc);
        await DotMarc.Demo.DemoDataSeeder.ResetAsync(context, dataset);
    }
}

// Must run first, before any other middleware that reads the request's scheme/host (redirects,
// authentication challenges, static files) — otherwise those still see the proxy's original
// (unforwarded) http request.
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapPost("/signout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);

    if (!demoOptions.Enabled)
    {
        await httpContext.SignOutAsync(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme);
    }

    return Results.Redirect("/");
}).RequireAuthorization();

if (demoOptions.Enabled)
{
    app.MapPost("/demo/sign-in/{persona}", async (string persona, HttpContext httpContext) =>
    {
        string email;
        string displayName;
        switch (persona)
        {
            case "admin":
                email = DotMarc.Demo.DemoDataSeeder.AdminEmail;
                displayName = "Demo Admin";
                break;
            case "viewer":
                email = DotMarc.Demo.DemoDataSeeder.ViewerEmail;
                displayName = $"Demo Viewer ({DotMarc.Demo.DemoDataSeeder.ViewerScopedGroupName})";
                break;
            default:
                return Results.BadRequest($"Unknown demo persona '{persona}'.");
        }

        // No antiforgery token: the only effect of this endpoint is changing which fixed demo
        // persona the calling browser's own session views as — there's no cross-user or
        // cross-tenant side effect a forged request could cause, so skipping CSRF protection
        // here (unlike every other mutating endpoint in this app, which goes through Blazor's
        // own antiforgery-protected form handling) is a deliberate, low-risk simplification.
        var identity = new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim("preferred_username", email),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, displayName)
            ],
            Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
            nameType: System.Security.Claims.ClaimTypes.Name,
            roleType: System.Security.Claims.ClaimTypes.Role);

        await httpContext.SignInAsync(
            Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
            new System.Security.Claims.ClaimsPrincipal(identity));

        return Results.Redirect("/");
    }).AllowAnonymous();
}

// Both endpoints below are hostname-routed (mta-sts.<domain>), not path-routed under this app's
// own hostname, so they're unauthenticated by necessity — Caddy and receiving mail servers are
// never signed in. Gating is done by looking up the Domain instead (see each endpoint).

// Caddy's on-demand-TLS "ask" callback: only let Caddy attempt certificate issuance for a
// hostname once DNS has actually been verified to point here, not the moment a customer merely
// enables hosting (PendingDns) — otherwise a typo'd or not-yet-propagated CNAME would burn a
// Let's Encrypt validation attempt against a hostname that doesn't resolve here yet.
app.MapGet("/.well-known/mta-sts-ask", async (string domain, IDbContextFactory<DotMarcDbContext> dbContextFactory) =>
{
    const string prefix = "mta-sts.";
    if (!domain.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return Results.NotFound();
    }

    await using var context = await dbContextFactory.CreateDbContextAsync();
    var domainName = domain[prefix.Length..];
    var isProvisionable = await context.Domains.AsNoTracking().AnyAsync(d =>
        d.Name == domainName &&
        d.MtaStsEnabled &&
        (d.MtaStsStatus == MtaStsStatus.PendingCertificate || d.MtaStsStatus == MtaStsStatus.Active || d.MtaStsStatus == MtaStsStatus.Failed));

    return isProvisionable ? Results.Ok() : Results.NotFound();
}).AllowAnonymous();

// The actual hosted policy, served on mta-sts.<domain> (matched by Host header, since this app
// has no other way to distinguish which of potentially many hosted domains a request is for).
app.MapGet("/.well-known/mta-sts.txt", async (HttpContext httpContext, IDbContextFactory<DotMarcDbContext> dbContextFactory) =>
{
    const string prefix = "mta-sts.";
    var host = httpContext.Request.Host.Host;
    if (!host.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return Results.NotFound();
    }

    await using var context = await dbContextFactory.CreateDbContextAsync();
    var domainName = host[prefix.Length..];
    var domain = await context.Domains.AsNoTracking().FirstOrDefaultAsync(d => d.Name == domainName);
    if (domain is null || !domain.MtaStsEnabled)
    {
        return Results.NotFound();
    }

    var policyText = MtaStsPolicyRenderer.Render(domain.MtaStsMode, domain.MtaStsMxHosts, domain.MtaStsMaxAgeSeconds);
    return Results.Text(policyText, "text/plain");
}).AllowAnonymous();

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
    var requiredPolicy = target switch { "mta-sts" => "MtaStsManage", "dmarc" or "tlsrpt" => "DomainsEdit", _ => null };
    if (requiredPolicy is null)
    {
        return Results.BadRequest();
    }

    var authResult = await authorizationService.AuthorizeAsync(httpContext.User, requiredPolicy);
    if (!authResult.Succeeded)
    {
        return Results.Forbid();
    }

    var pushProvider = await pushProviders.FindConfiguredAsync(provider);
    if (pushProvider is null)
    {
        return Results.NotFound();
    }

    var (codeVerifier, codeChallenge) = PkceGenerator.Generate();
    var state = stateProtector.Protect(domainId, target, codeVerifier, DateTimeOffset.UtcNow);
    var redirectUri = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/dns-push/{provider}/callback";

    return Results.Redirect(await pushProvider.BuildAuthorizationUrlAsync(state, codeChallenge, redirectUri));
});

app.MapGet("/dns-push/{provider}/callback", async (
    string provider, string? code, string? state, string? error, HttpContext httpContext,
    IEnumerable<IDnsPushProvider> pushProviders, DnsPushStateProtector stateProtector,
    IDbContextFactory<DotMarcDbContext> dbContextFactory, IDmarcTxtLookup dmarcTxtLookup, ITlsrptTxtLookup tlsrptTxtLookup,
    IOptions<DotMarc.MtaSts.MtaStsOptions> mtaStsOptions, IOptions<GraphOptions> graphOptions,
    DotMarc.MtaSts.IMtaStsHostProvisioner mtaStsHostProvisioner, DotMarc.MtaSts.IMtaStsCnameLookup mtaStsCnameLookup,
    IAuthorizationService authorizationService, ILogger<Program> logger) =>
{
    var pushProvider = await pushProviders.FindConfiguredAsync(provider);
    var decodedState = state is null ? null : stateProtector.Unprotect(state, DateTimeOffset.UtcNow);
    if (pushProvider is null || decodedState is null)
    {
        return DnsPushPopupResult.Close("invalid");
    }

    // The signed state proves the /start redirect was legitimate, but says nothing about whether
    // whoever's browser lands HERE still holds the permission the push actually needs — re-run the
    // same target-to-policy check /start already made rather than relying solely on the app's
    // FallbackPolicy (any authenticated user).
    var requiredPolicy = decodedState.PushTarget switch { "mta-sts" => "MtaStsManage", "dmarc" or "tlsrpt" => "DomainsEdit", _ => null };
    if (requiredPolicy is null)
    {
        return DnsPushPopupResult.Close("invalid");
    }
    var authResult = await authorizationService.AuthorizeAsync(httpContext.User, requiredPolicy);
    if (!authResult.Succeeded)
    {
        return DnsPushPopupResult.Close("invalid");
    }

    await using var context = await dbContextFactory.CreateDbContextAsync();
    var domain = await context.Domains.AsNoTracking().SingleOrDefaultAsync(d => d.Id == decodedState.DomainId);
    if (domain is null)
    {
        return DnsPushPopupResult.Close("invalid");
    }

    if (error is not null || code is null)
    {
        return DnsPushPopupResult.Close("cancelled");
    }

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
                var existingAsuid = await mtaStsCnameLookup.LookupAsuidTxtAsync(domain.Name, CancellationToken.None);
                var asuidChange = existingAsuid is null
                    ? new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"asuid.mta-sts.{domain.Name}", verificationId, null, domain.Name)
                    : new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"asuid.mta-sts.{domain.Name}", verificationId, existingAsuid, domain.Name);
                changes.Add(asuidChange);
            }
        }
    }
    else if (decodedState.PushTarget == "dmarc")
    {
        var existing = await dmarcTxtLookup.LookupAsync(domain.Name, CancellationToken.None);
        var mailbox = graphOptions.Value.MailboxAddress;
        if (existing.DelegatedToCname is not null)
        {
            // The record is a CNAME delegated to a third party — DNS doesn't allow a CNAME to
            // coexist with any other record type at the same name, so there's no in-place merge
            // here, only delete-then-create. The confirm dialog makes this explicit before the
            // user ever reaches this endpoint (DnsRecordPushDecision.NeedsConfirmation always
            // returns true when DelegatedToCname is set).
            changes = [new DnsRecordChange(DnsRecordChangeKind.Replace, "TXT", $"_dmarc.{domain.Name}", $"v=DMARC1; p=none; rua=mailto:{mailbox}", existing.DelegatedToCname, domain.Name, ExistingRecordType: "CNAME")];
        }
        else if (existing.DirectValue is null)
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
    else
    {
        var mailbox = graphOptions.Value.TlsrptMailboxAddress;
        if (string.IsNullOrWhiteSpace(mailbox))
        {
            return DnsPushPopupResult.Close("error");
        }

        var existing = await tlsrptTxtLookup.LookupAsync(domain.Name, CancellationToken.None);
        if (existing.DelegatedToCname is not null)
        {
            changes = [new DnsRecordChange(DnsRecordChangeKind.Replace, "TXT", $"_smtp._tls.{domain.Name}", $"v=TLSRPTv1; rua=mailto:{mailbox}", existing.DelegatedToCname, domain.Name, ExistingRecordType: "CNAME")];
        }
        else if (existing.DirectValue is null)
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

    var redirectUri = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/dns-push/{provider}/callback";
    var result = await pushProvider.ExchangeAndPushAsync(code, decodedState.CodeVerifier, redirectUri, changes, CancellationToken.None);

    var resultFlag = result.Outcome switch
    {
        DnsPushOutcome.Pushed => "pushed",
        DnsPushOutcome.ZoneNotFound => "zone-not-found",
        DnsPushOutcome.ReplaceFailedAfterDelete => "replace-failed",
        _ => "error"
    };

    // The popup's postMessage carries only this coarse flag — DetailMessage never reaches the
    // browser — so this is the only place the specific reason (status codes, provider error text)
    // survives at all. Logged at Warning for every non-Pushed outcome, not just
    // ReplaceFailedAfterDelete, since any of them can otherwise be silently undiagnosable.
    if (result.Outcome != DnsPushOutcome.Pushed)
    {
        logger.LogWarning("DNS push for domain {DomainId}, target {PushTarget} did not succeed: outcome={Outcome}, detail={DetailMessage}",
            decodedState.DomainId, decodedState.PushTarget, result.Outcome, result.DetailMessage);
    }

    return DnsPushPopupResult.Close(resultFlag);
});

// Unauthenticated by necessity — HaloPSA's own outbound webhook config isn't confirmed to support
// custom headers, so the shared secret travels in the path instead. A non-matching secret returns
// 404 rather than 401 so an unauthenticated caller can't even confirm this endpoint exists.
//
// Binds the raw HttpRequest rather than a typed HaloWebhookTicketPayload parameter — a typed body
// parameter is parsed by ASP.NET Core's model binder before the handler runs at all, which would
// 400 a malformed body regardless of whether the secret is even right. The secret check has to
// happen first, and body parsing happens only after it passes, inside the handler.
app.MapPost("/integrations/halopsa/webhook/{secret}", async (
    string secret, HttpRequest request, IDbContextFactory<DotMarcDbContext> dbContextFactory, ILogger<Program> logger) =>
{
    await using var context = await dbContextFactory.CreateDbContextAsync();
    var settings = await context.HaloPsaSettings.SingleAsync();

    if (string.IsNullOrEmpty(settings.WebhookSecret) ||
        !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(settings.WebhookSecret)))
    {
        return Results.NotFound();
    }

    HaloWebhookTicketPayload? payload;
    try
    {
        payload = await JsonSerializer.DeserializeAsync<HaloWebhookTicketPayload>(request.Body, cancellationToken: request.HttpContext.RequestAborted);
    }
    catch (JsonException ex)
    {
        // Nothing a retry from Halo would fix — log it and 200 rather than surfacing a failure
        // status that could trigger a retry storm.
        logger.LogWarning(ex, "Received an unparseable HaloPSA webhook payload.");
        return Results.Ok();
    }

    if (payload is null)
    {
        logger.LogWarning("Received an empty HaloPSA webhook payload.");
        return Results.Ok();
    }

    if (!HaloWebhookStatusMatcher.IsClosedStatus(payload, settings))
    {
        return Results.Ok();
    }

    var ticketId = payload.TicketId.ToString();
    var alert = await context.AlertEvents.FirstOrDefaultAsync(e =>
        e.ExternalTicketProvider == "HaloPSA" && e.ExternalTicketId == ticketId && !e.IsResolved);

    if (alert is not null)
    {
        alert.IsResolved = true;
        alert.ResolvedUtc = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync();
    }

    return Results.Ok();
}).AllowAnonymous();

app.MapRazorComponents<DotMarc.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
