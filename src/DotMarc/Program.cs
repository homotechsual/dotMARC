using DotMarc.Data;
using DotMarc.Graph;
using DotMarc.Ingestion;
using MudBlazor.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
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

builder.Services.AddOptions<GraphOptions>()
    .Bind(builder.Configuration.GetSection(GraphOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IGraphTokenProvider, ConfidentialClientGraphTokenProvider>();

builder.Services.AddHttpClient<IGraphMailboxClient, GraphMailboxClient>(client =>
{
    client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
});

// PollingService has two constructors (one for direct test construction, one for the real
// DI-scoped host path), both with 3 parameters. The built-in container's own constructor
// selection does NOT consult [ActivatorUtilitiesConstructor] when activating a plain
// AddHostedService<PollingService>() registration, so that alone throws "ambiguous
// constructors" here (both IGraphMailboxClient and DotMarcDbContext are also registered in
// this container). Routing activation through ActivatorUtilities.CreateInstance explicitly
// does honor that attribute, so it deterministically selects the host constructor.
builder.Services.AddHostedService<PollingService>(sp => ActivatorUtilities.CreateInstance<PollingService>(sp));

builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("EntraId"));

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await DatabaseMigrator.MigrateWithLeaderLockAsync(scope.ServiceProvider.GetRequiredService<DotMarcDbContext>());
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

app.MapRazorComponents<DotMarc.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
