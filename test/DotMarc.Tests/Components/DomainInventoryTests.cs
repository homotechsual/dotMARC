using System.Net;
using System.Reflection;
using DotMarc.Components.Pages;
using DotMarc.Data;
using DotMarc.Demo;
using DotMarc.Reporting;
using DotMarc.Psa;
using DotMarc.ServerLogs;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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
    public async Task NullDomainDkimLabelsRemainVisibleAndDoNotOfferSendingKeyPush()
    {
        using var client = await SignInAsync("admin");
        var factory = _factory.Services.GetRequiredService<IDbContextFactory<DotMarcDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var domain = new Domain
        {
            Name = "parked-health.example", IsMonitored = true, SpfCheckStatus = SpfCheckStatus.NullSpf,
            DmarcCheckStatus = DmarcCheckStatus.Ok, DmarcPolicy = DmarcPolicyLevel.Reject,
            DkimSelectors = ["old"], DkimCheckStatus = DkimCheckStatus.UnexpectedSendingKey,
            DkimCheckedUtc = DateTimeOffset.UtcNow, FirstSeenUtc = DateTimeOffset.UtcNow
        };
        domain.DkimRecords.Add(new DomainDkimRecord { Selector = "old", RecordType = DkimRecordType.Txt, Value = "v=DKIM1; p=ABC" });
        db.Domains.Add(domain);
        await db.SaveChangesAsync();
        Assert.Contains("Sending key on no-sender domain", await client.GetStringAsync("/dashboard?search=parked-health"));
        Assert.Contains("Sending key on no-sender domain", await client.GetStringAsync("/domains?search=parked-health"));
        var overview = await client.GetStringAsync("/domains/parked-health.example");
        Assert.Contains("Sending key on no-sender domain", overview);
        Assert.DoesNotContain(">Push DKIM records<", overview);
        domain.DkimCheckStatus = DkimCheckStatus.NoSendingKeys;
        await db.SaveChangesAsync();
        Assert.Contains("No keys at configured selectors", await client.GetStringAsync("/domains?search=parked-health"));
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
        var dashboard = await client.GetStringAsync("/dashboard");
        Assert.DoesNotContain("domain-inventory", dashboard);
        Assert.Contains("Domains needing attention", dashboard);
        Assert.DoesNotContain("missing expected reports", dashboard, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aria-label=\"Issue categories\"", dashboard);
        Assert.Contains("DNS/configuration", dashboard);
        Assert.Contains("Missing reports", dashboard);
        Assert.Contains("Quarantines and rejects to investigate", dashboard);
        Assert.Contains("data-domain-link", await client.GetStringAsync("/domains"));
        Assert.Contains("Show management columns", await client.GetStringAsync("/domains"));
    }

    [Fact]
    public async Task BaselineTabsExplainDefaultExceptionAndReviewFlow()
    {
        using var client = await SignInAsync("admin");
        var domain = await client.GetStringAsync("/baselines/domain");
        Assert.Contains("Saving a target changes", domain);
        Assert.Contains("Inherits system default", domain);
        Assert.Contains("Edit default target", domain);
        Assert.Contains("Set override", domain);
        Assert.Contains("Choose a domain in Inventory", domain);
        Assert.True(domain.IndexOf("Set the default", StringComparison.Ordinal) < domain.IndexOf("Add exceptions only where needed", StringComparison.Ordinal));
        Assert.True(domain.IndexOf("Add exceptions only where needed", StringComparison.Ordinal) < domain.IndexOf("Review and resolve gaps", StringComparison.Ordinal));
        Assert.Contains("href=\"/domains?target=below\"", domain);
        var sender = await client.GetStringAsync("/baselines/sender");
        Assert.Contains("Estate-wide sender decisions", sender);
        Assert.Contains("Where it comes from", sender);
        Assert.Contains("Review outstanding sender decisions", sender);
        Assert.Contains("attention=senders", sender);
        Assert.Contains("Choose a domain", sender);
        Assert.True(sender.IndexOf("Set default decisions", StringComparison.Ordinal) < sender.IndexOf("Review undecided senders", StringComparison.Ordinal));
        Assert.True(sender.IndexOf("Review undecided senders", StringComparison.Ordinal) < sender.IndexOf("Add exceptions where needed", StringComparison.Ordinal));
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
        Assert.Contains($"Domains in scope: {await db.Domains.CountAsync()}.", html);
    }

    [Fact]
    public async Task DashboardQueuesAndMetricsAreScoped_AndGlobalOperationsArePermissionGated()
    {
        using var client = await SignInAsync("viewer");
        var html = await client.GetStringAsync("/dashboard?group=all");
        Assert.Contains("aurora-retail.example", html);
        Assert.DoesNotContain("cobalt-freight.example", html);
        Assert.DoesNotContain("Integration and ingestion status", html);
        Assert.DoesNotContain("unparseable messages retained", html);
        Assert.DoesNotContain("Review integration settings", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "aria-controls=\"domain-filters\"").Cast<object>());
        var empty = await client.GetStringAsync("/dashboard?search=not-a-domain");
        Assert.Contains("Domains in scope: 0.", empty);
        Assert.Contains("No domains in the current scope.", empty);
        Assert.Contains("No message data is available", empty);
    }

    [Fact]
    public async Task DashboardSenderCountUsesDecisions_AndUpdatesWithFilters()
    {
        using var client = await SignInAsync("admin");
        var factory = _factory.Services.GetRequiredService<IDbContextFactory<DotMarcDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var domain = new Domain { Name = "action-centre-test.example", SpfCheckStatus = SpfCheckStatus.Ok,
            DmarcCheckStatus = DmarcCheckStatus.Ok, DmarcAuthorizationCheckStatus = DmarcAuthorizationCheckStatus.NotApplicable };
        domain.Reports.Add(new Report
        {
            ReportId = "dashboard-test", ReportingOrg = "receiver.example", ReceivedUtc = now.AddMinutes(-1), RawXml = "",
            Records = [new() { SourceIp = "192.0.2.240", HeaderFrom = domain.Name, MessageCount = 10 }]
        });
        db.Domains.Add(domain);
        await db.SaveChangesAsync();
        var html = await client.GetStringAsync("/dashboard?search=action-centre-test");
        Assert.Contains("Sender decisions outstanding: 1.", html);
        db.SenderDecisions.Add(new SenderDecision
        {
            Scope = SenderDecisionScope.Domain, DomainId = domain.Id, Kind = SenderDecisionKind.Ip, Key = "192.0.2.240",
            Status = SenderDecisionStatus.UnderReview, DecidedBy = "test", DecidedByName = "Test", DecidedUtc = now
        });
        await db.SaveChangesAsync();
        html = await client.GetStringAsync("/dashboard?search=action-centre-test");
        Assert.Contains("Sender decisions outstanding: 0.", html);
        Assert.DoesNotContain("Review 1 sender decisions", html);
    }

    [Fact]
    public async Task DashboardShowsIntegrationFailuresWithoutCallingRemoteProviders_AndLabelsLogLimitations()
    {
        var store = new InMemoryLogStore(true);
        store.Add(DateTimeOffset.UtcNow, LogLevel.Warning, "DotMarc.Psa.PsaTicketPoller",
            "Checking HaloPSA tickets failed: token=private-value", null);
        store.Add(DateTimeOffset.UtcNow.AddDays(-2), LogLevel.Error, "DotMarc.Psa.PsaTicketPoller", "Expired failure should not display", null);
        store.Add(DateTimeOffset.UtcNow, LogLevel.Error, "Unrelated.Component", "Unrelated failure should not display", null);
        await using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPsaProvider>();
            services.AddSingleton<IPsaProvider>(new IncompletePsa());
            services.RemoveAll<InMemoryLogStore>();
            services.AddSingleton(store);
        }));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync("/demo/sign-in/admin", null);
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<DotMarcDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        db.PollCycles.Add(new PollCycle { PolledUtc = DateTimeOffset.UtcNow.AddDays(1), Succeeded = false, ErrorMessage = "Test polling failure" });
        await db.SaveChangesAsync();
        var html = await client.GetStringAsync("/dashboard?search=not-a-domain");
        Assert.Contains("enabled but not ready", html);
        Assert.Contains("client secret", html);
        Assert.Contains("Mailbox polling failed", html);
        Assert.Contains("Checking HaloPSA tickets failed", html);
        Assert.Contains("[redacted]", html);
        Assert.DoesNotContain("private-value", html);
        Assert.DoesNotContain("Expired failure should not display", html);
        Assert.DoesNotContain("Unrelated failure should not display", html);
        Assert.Contains("Historical events may already be resolved", html);
        Assert.Contains("independent of domain filters", html);
    }

    private sealed class IncompletePsa : IPsaProvider
    {
        public PsaKind Kind => PsaKind.HaloPsa;
        public Task<PsaReadiness> GetReadinessAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PsaReadiness(true, ["client secret"]));
        public Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Dashboard must not query a remote PSA.");
        public Task<string> CreateTicketAsync(DotMarcDbContext context, PsaTicketRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PsaTicketState> GetTicketStateAsync(DotMarcDbContext context, string ticketId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CloseTicketAsync(DotMarcDbContext context, string ticketId, string note, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetTicketUrlTemplateAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
