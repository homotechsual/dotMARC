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

builder.Services.AddHostedService<PollingService>();

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
