using System.Net;
using System.Reflection;
using DotMarc.Components.Pages;
using DotMarc.Data;
using DotMarc.Demo;
using DotMarc.Reporting;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotMarc.Tests.Components;

[Collection("Postgres")]
public sealed class DomainInventoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private IAsyncDisposable? _cleanup;
    private WebApplicationFactory<Program> _factory = null!;
    public async Task InitializeAsync()
    {
        var (connection, cleanup) = await fixture.CreateDatabaseAsync();
        _cleanup = cleanup;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DotMarc", connection);
            builder.UseSetting("Demo:Enabled", "true");
        });
    }
    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        if (_cleanup is not null) await _cleanup.DisposeAsync();
    }
    private async Task<HttpClient> SignInAsync(string persona)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync($"/demo/sign-in/{persona}", null);
        return client;
    }

    [Fact]
    public async Task InventoryIsViewableByScopedViewer_WithoutWriteControlsOrHiddenDomains()
    {
        Assert.Equal("DomainsView", Assert.Single(typeof(DomainInventory).GetCustomAttributes<AuthorizeAttribute>()).Policy);
        using var client = await SignInAsync("viewer");
        var html = await client.GetStringAsync("/domains?group=all&view=group");
        Assert.Contains("aurora-retail.example", html);
        Assert.DoesNotContain("cobalt-freight.example", html);
        Assert.DoesNotContain(">Add domain<", html);
        Assert.DoesNotContain(">Import domains<", html);
        Assert.DoesNotContain("aria-label=\"Monitor ", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "aria-controls=\"domain-filters\"").Cast<object>());
    }

    [Fact]
    public async Task ChangedGroupAccessIsRechecked_EvenWhenTheUrlNamesTheOldGroup()
    {
        using var client = await SignInAsync("viewer");
        var factory = _factory.Services.GetRequiredService<IDbContextFactory<DotMarcDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var person = await db.UserAccesses.Include(user => user.ScopedGroups).SingleAsync(user => user.Email == DemoDataSeeder.ViewerEmail);
        var originalGroup = Assert.Single(person.ScopedGroups).Id;
        var replacement = await db.Groups.Include(group => group.Domains).FirstAsync(group => group.Id != originalGroup && group.Domains.Count > 0);
        var replacementDomain = replacement.Domains.First().Name;
        person.ScopedGroups.Clear();
        person.ScopedGroups.Add(replacement);
        await db.SaveChangesAsync();
        var html = await client.GetStringAsync($"/domains?group={originalGroup}&view=group");
        Assert.DoesNotContain(">aurora-retail.example</a>", html);
        Assert.Contains($">{replacementDomain}</a>", html);
    }

    [Fact]
    public async Task GroupedViewHasOneSetOfFilters_AndFiltersBeforeGrouping()
    {
        using var client = await SignInAsync("admin");
        var html = await client.GetStringAsync("/domains?view=group&search=aurora");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "aria-controls=\"domain-filters\"").Cast<object>());
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "id=\"domain-filters\"").Cast<object>());
        Assert.Contains("aurora-retail.example", html);
        Assert.DoesNotContain(">cobalt-freight.example</a>", html);
        Assert.DoesNotContain(">Cobalt Freight (", html);
    }

    [Fact]
    public async Task DashboardHasNoInventoryTable_AndInventoryRetainsManagementProjection()
    {
        using var client = await SignInAsync("admin");
        Assert.DoesNotContain("data-domain-link", await client.GetStringAsync("/dashboard"));
        Assert.Contains("data-domain-link", await client.GetStringAsync("/domains"));
        Assert.Contains("Show management columns", await client.GetStringAsync("/domains"));
    }

    [Fact]
    public async Task SavedDefaultsAreIndependent_AndLegacyDataMigratesOnExplicitSave()
    {
        using var client = await SignInAsync("admin");
        var factory = _factory.Services.GetRequiredService<IDbContextFactory<DotMarcDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.UserDashboardPreferences.Add(new UserDashboardPreference { UserKey = "test-user", SettingsJson = """{"groupedView":true,"minPassRatePercent":95}""" });
        await db.SaveChangesAsync();
        Assert.True((await DashboardPreferencesStore.GetListsAsync(db, "test-user")).Inventory!.GroupedView);
        await DashboardPreferencesStore.SaveListAsync(db, "test-user", true, new() { Target = DotMarc.Baselines.DmarcTargetOutcome.Below, AllMyGroups = true, Search = "not saved" });
        var saved = await DashboardPreferencesStore.GetListsAsync(db, "test-user");
        Assert.Equal(95, saved.Dashboard!.MinPassRatePercent);
        Assert.Null(saved.Inventory!.Search);
        Assert.True(saved.Inventory.AllMyGroups);
        await DashboardPreferencesStore.SaveListAsync(db, "test-user", false, null);
        Assert.NotNull((await DashboardPreferencesStore.GetListsAsync(db, "test-user")).Inventory);
        var stored = await db.UserDashboardPreferences.SingleAsync(row => row.UserKey == "test-user");
        stored.SettingsJson = """{"version":99}""";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<FormatException>(() => DashboardPreferencesStore.SaveListAsync(db, "test-user", true, null));
        Assert.Equal("""{"version":99}""", await db.UserDashboardPreferences.AsNoTracking().Where(row => row.UserKey == "test-user").Select(row => row.SettingsJson).SingleAsync());
    }

    [Theory]
    [InlineData("sort=groups")]
    [InlineData("minReports=5000")]
    public async Task DashboardDoesNotApplyHiddenManagementCriteria(string query)
    {
        using var client = await SignInAsync("admin");
        var html = await client.GetStringAsync($"/dashboard?{query}");
        Assert.Contains($"href=\"{WebUtility.HtmlEncode(new DomainListState { ShowFilters = true }.ToUrl("/domains"))}\"", html);
        var factory = _factory.Services.GetRequiredService<IDbContextFactory<DotMarcDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var text = System.Text.RegularExpressions.Regex.Replace(WebUtility.HtmlDecode(html), @"<[^>]*>|\s+", "");
        Assert.Contains($"{await db.Domains.CountAsync()}domainsinscope", text);
    }

    [Fact]
    public async Task TagOnlyAdministratorCanReachTags_ButNotGroupManagement()
    {
        using var admin = await SignInAsync("admin");
        var factory = _factory.Services.GetRequiredService<IDbContextFactory<DotMarcDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var person = await db.UserAccesses.SingleAsync(user => user.Email == DemoDataSeeder.ViewerEmail);
        person.Role = new Role { Name = "Tag administrator", Permissions = [Permission.DomainsView, Permission.TagsEdit] };
        await db.SaveChangesAsync();
        using var client = await SignInAsync("viewer");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/tags")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/groups")).StatusCode);
        var html = await client.GetStringAsync("/tags");
        Assert.Contains(">Tags<", html);
        Assert.DoesNotContain(">Add tag<", html);
    }
}
