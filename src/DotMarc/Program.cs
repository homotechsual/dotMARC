using DotMarc.Data;
using DotMarc.Graph;
using DotMarc.Ingestion;
using MudBlazor.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddDbContext<DotMarcDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DotMarc")
        ?? "Data Source=dotmarc.db"));

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

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<DotMarcDbContext>().Database.EnsureCreated();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<DotMarc.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
