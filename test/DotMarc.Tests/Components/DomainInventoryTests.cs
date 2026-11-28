using System.Net;
using System.Reflection;
using System.Security.Claims;
using DotMarc.Audit;
using DotMarc.Baselines;
using DotMarc.Dns;
using DotMarc.Security;
using DotMarc.Components.Pages;
using DotMarc.Data;
using DotMarc.Demo;
using DotMarc.DnsPush;
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
using MudBlazor;
using Xunit;

namespace DotMarc.Tests.Components;

[Collection("Postgres")]
public sealed class DomainInventoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private IAsyncDisposable? _cleanup;
    private WebApplicationFactory<Program> _factory = null!;
    private readonly FakeDnsPushProvider _dnsPush = new("cloudflare");
    private readonly FakeSpfDnsChecker _bulkSpf = new();
    private readonly FakeDmarcDnsChecker _bulkDmarc = new();
    private readonly FakeDkimDnsChecker _bulkDkim = new();
    private readonly FakeDnsProviderDetector _bulkProvider = new();
    public async Task InitializeAsync()
    {
        var (connection, cleanup) = await fixture.CreateDatabaseAsync();
        _cleanup = cleanup;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DotMarc", connection);
            builder.UseSetting("Demo:Enabled", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDnsPushProvider>();
                services.AddSingleton<IDnsPushProvider>(_dnsPush);
                services.RemoveAll<ISpfDnsChecker>();
                services.RemoveAll<IMxDnsChecker>();
                services.RemoveAll<IDmarcDnsChecker>();
                services.RemoveAll<ITlsrptDnsChecker>();
                services.RemoveAll<IDkimDnsChecker>();
                services.RemoveAll<IDnsProviderDetector>();
                services.AddSingleton<ISpfDnsChecker>(_bulkSpf);
                services.AddSingleton<IMxDnsChecker>(new FakeMxDnsChecker());
                services.AddSingleton<IDmarcDnsChecker>(_bulkDmarc);
                services.AddSingleton<ITlsrptDnsChecker>(new FakeTlsrptDnsChecker());
                services.AddSingleton<IDkimDnsChecker>(_bulkDkim);
                services.AddSingleton<IDnsProviderDetector>(_bulkProvider);
            });
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
    public async Task ServerLogsRenderCompleteLongValuesWithResponsiveLabels()
    {
        var store = new InMemoryLogStore(true);
        var message = "Message " + new string('x', 500);
        var exception = "Exception " + new string('y', 500);
        store.Add(DateTimeOffset.UtcNow, LogLevel.Warning, "Long.Category." + new string('z', 200), message, exception);
        await using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<InMemoryLogStore>();
            services.AddSingleton(store);
        }));
        using var client = factory.CreateClient();
        await client.PostAsync("/demo/sign-in/admin", null);
        var html = await client.GetStringAsync("/logs/server");
        Assert.Contains("responsive-detail-table", html);
        foreach (var label in new[] { "Time (UTC)", "Level", "Source", "Message" })
            Assert.Contains($"data-label=\"{label}\"", html);
        Assert.Contains(message, html);
        Assert.Contains(exception, html);
        Assert.DoesNotContain("white-space:nowrap", html);
    }

    private static ClaimsPrincipal BulkUser(IEnumerable<Permission> permissions, IEnumerable<int>? groups = null, bool portal = false)
    {
        var claims = permissions.Select(permission => new Claim(UserAccessClaimsTransformation.PermissionClaimType, permission.ToString())).ToList();
        claims.AddRange((groups ?? []).Select(id => new Claim(UserAccessClaimsTransformation.ScopedGroupClaimType, id.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        if (portal) claims.Add(new(UserAccessClaimsTransformation.ClientPortalClaimType, "true"));
        return new(new ClaimsIdentity(claims, "Test"));
    }

    [Theory]
    [InlineData(Permission.DomainsDelete, true, false, true)]
    [InlineData(Permission.BaselinesManage, true, false, false)]
    [InlineData(Permission.DomainsEdit, true, true, false)]
    [InlineData(Permission.MtaStsManage, false, true, false)]
    public async Task SelectionAndDetailActionsReflectIndividualWritePermissions(Permission permission, bool bulk, bool dns, bool remove)
    {
        using var client = await SignInAsync("viewer");
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<DotMarcDbContext>>().CreateDbContextAsync();
        var viewer = await db.UserAccesses.Include(user => user.Role).SingleAsync(user => user.Email == DemoDataSeeder.ViewerEmail);
        viewer.Role.Permissions.Add(permission);
        await db.SaveChangesAsync();
        var list = await client.GetStringAsync("/domains");
        Assert.Equal(bulk, list.Contains("Bulk actions", StringComparison.Ordinal));
        Assert.Equal(dns, list.Contains("Review selected DNS", StringComparison.Ordinal));
        Assert.Contains("Select shop.aurora-retail.example", list);
        var detail = await client.GetStringAsync("/domains/shop.aurora-retail.example");
        Assert.Equal(remove, detail.Contains("Remove domain", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BulkActionsRecheckPermissionsScopeAndPreserveUnrelatedMemberships()
    {
        using var client = await SignInAsync("admin");
        var service = _factory.Services.GetRequiredService<BulkDomainActionService>();
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<DotMarcDbContext>>().CreateDbContextAsync();
        var visible = new Group { Name = "Bulk visible" };
        var second = new Group { Name = "Bulk second" };
        var hidden = new Group { Name = "Bulk hidden" };
        var existingTag = new Tag { Name = "Bulk existing", Color = Color.Primary };
        var newTag = new Tag { Name = "Bulk added", Color = Color.Secondary };
        var domain = new Domain { Name = "bulk-scope.example", Groups = [visible, hidden], Tags = [existingTag] };
        db.Domains.Add(domain);
        db.Groups.Add(second);
        db.Tags.Add(newTag);
        await db.SaveChangesAsync();
        var actor = AuditActor.ForSystem("Bulk test");
        var editor = BulkUser([Permission.DomainsView, Permission.DomainsEdit, Permission.DomainsDelete, Permission.BaselinesManage], [visible.Id, second.Id]);
        var viewOnly = BulkUser([Permission.DomainsView], [visible.Id]);
        var portal = BulkUser(Enum.GetValues<Permission>(), [visible.Id], portal: true);
        foreach (var action in Enum.GetValues<BulkDomainAction>())
        {
            var request = new BulkDomainActionRequest(action, [newTag.Id]);
            Assert.Equal(DomainActionOutcome.Skipped, (await service.ExecuteAsync(domain.Id, request, viewOnly, actor)).Outcome);
            Assert.Equal(DomainActionOutcome.Skipped, (await service.ExecuteAsync(domain.Id, request, portal, actor)).Outcome);
            Assert.Equal(DomainActionOutcome.Skipped, (await service.ExecuteAsync(domain.Id, request, BulkUser(Enum.GetValues<Permission>(), [second.Id]), actor)).Outcome);
        }
        Assert.DoesNotContain(domain.Name, _bulkProvider.CheckedDomains);
        Assert.DoesNotContain(domain.Name, _bulkSpf.CheckedDomains);
        Assert.Equal(DomainActionOutcome.Succeeded, (await service.ExecuteAsync(domain.Id, new(BulkDomainAction.AddGroups, [second.Id]), editor, actor)).Outcome);
        Assert.Equal(DomainActionOutcome.Succeeded, (await service.ExecuteAsync(domain.Id, new(BulkDomainAction.RemoveGroups, [visible.Id]), editor, actor)).Outcome);
        Assert.Equal(DomainActionOutcome.Skipped, (await service.ExecuteAsync(domain.Id, new(BulkDomainAction.RemoveGroups, [second.Id]), editor, actor)).Outcome);
        Assert.Equal(DomainActionOutcome.Skipped, (await service.ExecuteAsync(domain.Id, new(BulkDomainAction.AddGroups, [hidden.Id]), editor, actor)).Outcome);
        Assert.Equal(DomainActionOutcome.Succeeded, (await service.ExecuteAsync(domain.Id, new(BulkDomainAction.AddTags, [newTag.Id]), editor, actor)).Outcome);
        Assert.Equal(DomainActionOutcome.Skipped, (await service.ExecuteAsync(domain.Id, new(BulkDomainAction.RemoveTags, [int.MaxValue]), editor, actor)).Outcome);
        db.ChangeTracker.Clear();
        var stored = await db.Domains.Include(item => item.Groups).Include(item => item.Tags).AsSplitQuery().SingleAsync(item => item.Id == domain.Id);
        Assert.Equal(new[] { second.Id, hidden.Id }.Order(), stored.Groups.Select(group => group.Id).Order());
        Assert.Equal(new[] { existingTag.Id, newTag.Id }.Order(), stored.Tags.Select(tag => tag.Id).Order());
        Assert.Equal(DomainActionOutcome.Succeeded, (await service.ExecuteAsync(domain.Id, new(BulkDomainAction.RemoveTags, [newTag.Id]), editor, actor)).Outcome);
        db.ChangeTracker.Clear();
        stored = await db.Domains.Include(item => item.Groups).Include(item => item.Tags).AsSplitQuery().SingleAsync(item => item.Id == domain.Id);
        Assert.Equal(existingTag.Id, Assert.Single(stored.Tags).Id);
        stored.Groups.Clear();
        await db.SaveChangesAsync();
        Assert.Equal(DomainActionOutcome.Skipped, (await service.ExecuteAsync(domain.Id, new(BulkDomainAction.Remove, []), editor, actor)).Outcome);
        Assert.Equal(DomainActionOutcome.Skipped, (await service.ExecuteAsync(int.MaxValue, new(BulkDomainAction.Remove, []), editor, actor)).Outcome);
        Assert.True(await db.Domains.AnyAsync(item => item.Id == domain.Id));
        Assert.True(await db.AuditEntries.AnyAsync(entry => entry.ActorName == "Bulk test"));
    }

    [Fact]
    public async Task BulkTargetsCanBeInheritedAndRemovalDeletesFullDmarcAndTlsHistoryWithAudit()
    {
        using var client = await SignInAsync("admin");
        var service = _factory.Services.GetRequiredService<BulkDomainActionService>();
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<DotMarcDbContext>>().CreateDbContextAsync();
        var domain = new Domain { Name = "bulk-delete.example", FirstSeenUtc = DateTimeOffset.UtcNow.AddYears(-1) };
        domain.Reports.Add(new Report { ReportingOrg = "Old reporter", ReportId = "old", RawXml = "<report/>", ReceivedUtc = DateTimeOffset.UtcNow.AddMonths(-6) });
        domain.TlsrptReports.Add(new TlsrptReport { ReportingOrg = "TLS reporter", ReportId = "old-tls", RawJson = "{}", ReceivedUtc = DateTimeOffset.UtcNow.AddMonths(-6) });
        db.Domains.Add(domain);
        await db.SaveChangesAsync();
        var user = BulkUser([Permission.DomainsView, Permission.BaselinesManage, Permission.DomainsDelete]);
        var actor = AuditActor.ForSystem("Bulk delete test");
        Assert.Equal(DomainActionOutcome.Succeeded, (await service.ExecuteAsync(domain.Id, new(BulkDomainAction.SetTarget, [], new(DmarcPolicyLevel.Reject, DmarcPolicyLevel.Reject)), user, actor)).Outcome);
        await db.Entry(domain).ReloadAsync();
        Assert.Equal(DmarcPolicyLevel.Reject, domain.TargetDmarcPolicy);
        Assert.Null(domain.TargetDmarcSubdomainPolicy);
        Assert.Equal(DomainActionOutcome.Skipped, (await service.ExecuteAsync(domain.Id, new(BulkDomainAction.SetTarget, [], new((DmarcPolicyLevel)999, null)), user, actor)).Outcome);
        Assert.Equal(DomainActionOutcome.Succeeded, (await service.ExecuteAsync(domain.Id, new(BulkDomainAction.SetTarget, []), user, actor)).Outcome);
        await db.Entry(domain).ReloadAsync();
        Assert.Null(domain.TargetDmarcPolicy);
        var html = await client.GetStringAsync("/domains/bulk-delete.example");
        Assert.Contains("Remove domain", html);
        Assert.Equal(DomainActionOutcome.Succeeded, (await service.ExecuteAsync(domain.Id, new(BulkDomainAction.Remove, []), user, actor)).Outcome);
        Assert.False(await db.Domains.AnyAsync(item => item.Id == domain.Id));
        Assert.False(await db.Reports.AnyAsync(report => report.DomainId == domain.Id));
        Assert.False(await db.TlsrptReports.AnyAsync(report => report.DomainId == domain.Id));
        Assert.True(await db.AuditEntries.AnyAsync(entry => entry.Action == AuditActions.DomainRemoved && entry.ActorName == actor.Name));
    }

    [Fact]
    public async Task BulkDnsChecksRespectNullSendersPersistFailuresAndContinueOnNextDomain()
    {
        using var client = await SignInAsync("admin");
        var service = _factory.Services.GetRequiredService<BulkDomainActionService>();
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<DotMarcDbContext>>().CreateDbContextAsync();
        var domain = new Domain
        {
            Name = "bulk-dns.example", DkimSelectors = ["selector1"],
            DkimRecords = [new DomainDkimRecord { Selector = "selector1", RecordType = DkimRecordType.Txt, Value = "v=DKIM1; p=" }],
            MtaStsEnabled = true, MtaStsStatus = MtaStsStatus.PendingDns
        };
        db.Domains.Add(domain);
        await db.SaveChangesAsync();
        _bulkSpf.Result = new(SpfCheckStatus.NullSpf, null);
        var user = BulkUser([Permission.DomainsView, Permission.DomainsEdit]);
        var actor = AuditActor.ForSystem("Bulk DNS test");
        Assert.Equal(DomainActionOutcome.Succeeded, (await service.ExecuteAsync(domain.Id, new(BulkDomainAction.CheckDns, []), user, actor)).Outcome);
        await db.Entry(domain).ReloadAsync();
        Assert.NotNull(domain.SpfCheckedUtc);
        Assert.NotNull(domain.MxCheckedUtc);
        Assert.NotNull(domain.DmarcCheckedUtc);
        Assert.NotNull(domain.DkimCheckedUtc);
        Assert.NotNull(domain.TlsrptCheckedUtc);
        Assert.False(_bulkDmarc.ExpectsReports);
        Assert.True(_bulkDkim.ExpectsNoSenders);
        Assert.Equal("v=DKIM1; p=", Assert.Single(_bulkDkim.LastExpectedRecords!).Value);
        Assert.Empty(_bulkDmarc.AuthorizationCheckedDomains);
        Assert.Equal(DmarcAuthorizationCheckStatus.NotApplicable, domain.DmarcAuthorizationCheckStatus);
        Assert.Equal(MtaStsStatus.PendingDns, domain.MtaStsStatus);
        Assert.Null(domain.MtaStsCheckedUtc);
        var previousCheck = domain.SpfCheckedUtc;
        _bulkDmarc.ShouldThrow = true;
        Assert.Equal(DomainActionOutcome.Failed, (await service.ExecuteAsync(domain.Id, new(BulkDomainAction.CheckDns, []), user, actor)).Outcome);
        await db.Entry(domain).ReloadAsync();
        Assert.Equal(previousCheck, domain.SpfCheckedUtc);
        _bulkDmarc.ShouldThrow = false;
        _bulkProvider.ShouldThrow = true;
        Assert.Equal(DomainActionOutcome.Failed, (await service.ExecuteAsync(domain.Id, new(BulkDomainAction.RecheckProviders, []), user, actor)).Outcome);
        _bulkProvider.ShouldThrow = false;
        Assert.Equal(DomainActionOutcome.Succeeded, (await service.ExecuteAsync(domain.Id, new(BulkDomainAction.RecheckProviders, []), user, actor)).Outcome);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteAsync(domain.Id, new(BulkDomainAction.CheckDns, []), user, actor, stop.Token));
    }

    [Fact]
    public async Task WarningAndErrorStatuses_UseSolidIcons_AndKeepTheirAccessibleLabels()
    {
        using var client = await SignInAsync("admin");
        var html = await client.GetStringAsync("/domains");
        var statuses = System.Text.RegularExpressions.Regex.Matches(html,
            "<span\\b[^>]*class=\"domain-status-icon\"[^>]*>.*?</span>",
            System.Text.RegularExpressions.RegexOptions.Singleline).Select(match => match.Value).ToList();
        var errors = statuses.Where(status => status.Contains("mud-error-text", StringComparison.Ordinal)).ToList();
        var warnings = statuses.Where(status => status.Contains("mud-warning-text", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(errors);
        Assert.NotEmpty(warnings);
        Assert.All(errors, status => Assert.Contains(Icons.Material.Filled.Error, status));
        Assert.All(warnings, status => Assert.Contains(Icons.Material.Filled.Warning, status));
        Assert.All(statuses, status => Assert.Matches("role=\"img\" aria-label=\"[^\"]+\"", status));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public async Task CombinedDnsSetupRequiresBothWritePermissions(bool edit, bool mta, bool allowed)
    {
        using var client = await SignInAsync("admin");
        var claims = new List<System.Security.Claims.Claim>();
        if (edit) claims.Add(new(DotMarc.Security.UserAccessClaimsTransformation.PermissionClaimType, nameof(Permission.DomainsEdit)));
        if (mta) claims.Add(new(DotMarc.Security.UserAccessClaimsTransformation.PermissionClaimType, nameof(Permission.MtaStsManage)));
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "Test"));
        var authorization = _factory.Services.GetRequiredService<IAuthorizationService>();
        Assert.Equal(allowed, (await authorization.AuthorizeAsync(user, "DomainDnsSetup")).Succeeded);
    }

    [Fact]
    public async Task CombinedSetupRechecksDomainScopeAtStartAndCallback()
    {
        using var client = await SignInAsync("viewer");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<DotMarcDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var viewer = await db.UserAccesses.Include(user => user.Role).SingleAsync(user => user.Email == DemoDataSeeder.ViewerEmail);
        viewer.Role.Permissions.AddRange([Permission.DomainsEdit, Permission.MtaStsManage]);
        var allowed = await db.Domains.Include(domain => domain.Groups).SingleAsync(domain => domain.Name == "shop.aurora-retail.example");
        var other = await db.Domains.SingleAsync(domain => domain.Name == "cobalt-freight.example");
        await db.SaveChangesAsync();
        var payload = new string('a', 64);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/dns-push/cloudflare/start?domainId={allowed.Id}&target=domain-setup")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/dns-push/cloudflare/start?domainId={other.Id}&target=domain-setup&payload={payload}")).StatusCode);
        var start = await client.GetAsync($"/dns-push/cloudflare/start?domainId={allowed.Id}&target=domain-setup&payload={payload}");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(start.Headers.Location!.Query)["state"].ToString();
        allowed.Groups.Clear();
        await db.SaveChangesAsync();
        var callback = await client.GetStringAsync($"/dns-push/cloudflare/callback?code=test-code&state={Uri.EscapeDataString(state)}");
        Assert.Contains("Push not started", callback);
        Assert.Equal(0, _dnsPush.ExchangeCalls);
        Assert.Empty(_dnsPush.Pushes);
    }

    [Theory]
    [InlineData("/dashboard")]
    [InlineData("/domains")]
    [InlineData("/domains/import")]
    public async Task DomainEntryUsesTheSharedFormAndCarriesGroupContext(string route)
    {
        using var client = await SignInAsync("admin");
        var factory = _factory.Services.GetRequiredService<IDbContextFactory<DotMarcDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var group = await db.Groups.SingleAsync(candidate => candidate.Name == "Aurora Retail");
        var html = await client.GetStringAsync($"{route}?group={group.Id}");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "id=\"add-domain\"").Cast<object>());
        Assert.Contains("Domain name", html);
        Assert.Contains("Add to group", html);
        Assert.Contains(">Advanced</summary>", html);
        Assert.Contains("Open domain and set up DNS", html);
        Assert.Contains("DKIM selectors", html);
        Assert.Contains("value=\"Aurora Retail\"", html);
        Assert.Contains("no baseline setup is required", html);
        if (route != "/domains/import")
            Assert.Contains($"/domains/import?group={group.Id}", html);
    }

    [Theory]
    [InlineData("/dashboard")]
    [InlineData("/domains")]
    public async Task DomainEntryIsHiddenForReadOnlyUsers(string route)
    {
        using var client = await SignInAsync("viewer");
        var html = await client.GetStringAsync(route);
        Assert.DoesNotContain("id=\"add-domain\"", html);
        Assert.DoesNotContain("Add to group", html);
        Assert.DoesNotContain(">Import domains<", html);
    }

    [Theory]
    [InlineData("admin", true)]
    [InlineData("viewer", false)]
    public async Task OverviewOffersScopedGroupAndSenderControlsOnlyToEditors(string persona, bool editor)
    {
        using var client = await SignInAsync(persona);
        var overview = await client.GetStringAsync("/domains/aurora-retail.example");
        Assert.Equal(editor, overview.Contains("Remove domain", StringComparison.Ordinal));
        Assert.Contains("domain-groups", overview);
        Assert.Equal(editor, overview.Contains(">Change groups<", StringComparison.Ordinal));
        Assert.Equal(editor, overview.Contains("Review sender decisions for aurora-retail.example", StringComparison.Ordinal));
        if (!editor)
        {
            Assert.Contains("View sender evidence for aurora-retail.example", overview);
            Assert.Contains("a sender reviewer must record decisions", overview);
        }
        var sources = await client.GetStringAsync("/domains/aurora-retail.example/sources");
        Assert.Contains("Source IPs &amp; evidence", sources);
        Assert.Contains("Show source IPs and evidence for", sources);
        Assert.Equal(editor, sources.Contains("Decide on service", StringComparison.Ordinal) || sources.Contains("Change decision for service", StringComparison.Ordinal));
        if (!editor) Assert.Contains("You do not have permission to change decisions", sources);
    }

    [Fact]
    public async Task ScopedOverviewEditorSeesOnlyAccessibleMembershipNames()
    {
        using var client = await SignInAsync("viewer");
        var factory = _factory.Services.GetRequiredService<IDbContextFactory<DotMarcDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var person = await db.UserAccesses.Include(user => user.Role).SingleAsync(user => user.Email == DemoDataSeeder.ViewerEmail);
        person.Role.Permissions.Add(Permission.DomainsEdit);
        var domain = await db.Domains.Include(candidate => candidate.Groups).SingleAsync(candidate => candidate.Name == "shop.aurora-retail.example");
        domain.Groups.Add(await db.Groups.SingleAsync(group => group.Name == "Cobalt Freight"));
        await db.SaveChangesAsync();

        var overview = await client.GetStringAsync("/domains/shop.aurora-retail.example");
        Assert.Contains(">Change groups<", overview);
        Assert.Contains(">Aurora Retail</p>", overview);
        var groups = overview[overview.IndexOf("domain-groups", StringComparison.Ordinal)..overview.IndexOf(">Change groups<", StringComparison.Ordinal)];
        Assert.DoesNotContain("Cobalt Freight", groups);
        Assert.Contains("View sender evidence for shop.aurora-retail.example", overview);
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
        Assert.DoesNotContain("Recheck selected providers", html);
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
    public async Task OperationalStatusCollapsesOnlyWithKnownSuccessfulPolling()
    {
        await using var statusFactory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.RemoveAll<IPsaProvider>()));
        using var client = statusFactory.CreateClient();
        await client.PostAsync("/demo/sign-in/admin", null);
        var factory = statusFactory.Services.GetRequiredService<IDbContextFactory<DotMarcDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.ParseFailures.ExecuteDeleteAsync();
        await db.PollCycles.ExecuteDeleteAsync();
        Assert.True(await ExpandedAsync());
        var poll = new PollCycle { PolledUtc = DateTimeOffset.UtcNow, Succeeded = true };
        db.PollCycles.Add(poll);
        await db.SaveChangesAsync();
        Assert.False(await ExpandedAsync());
        poll.Succeeded = false;
        await db.SaveChangesAsync();
        Assert.True(await ExpandedAsync());

        async Task<bool> ExpandedAsync()
        {
            var html = await client.GetStringAsync("/dashboard");
            var start = html.IndexOf("dashboard-operations", StringComparison.Ordinal);
            Assert.True(start >= 0);
            return System.Text.RegularExpressions.Regex.IsMatch(html[start..], @"<details\s+open(?:=|\s|>)");
        }
    }

    [Fact]
    public async Task ExpandedQueuesUseTheSameScopedDomainSetsAsDashboard()
    {
        using var client = await SignInAsync("viewer");
        foreach (var (queue, categories, css) in new[]
        {
            ("attention", "", "dashboard-attention"),
            ("attention", "&attention=senders", "dashboard-attention"),
            ("attention", "&attention=dns,missing", "dashboard-attention"),
            ("investigate", "", "dashboard-investigations")
        })
        {
            var dashboard = await client.GetStringAsync($"/dashboard?group=all{categories}");
            var start = dashboard.IndexOf(css, StringComparison.Ordinal);
            var tableEnd = dashboard.IndexOf("</table>", start, StringComparison.Ordinal);
            Assert.True(start >= 0 && tableEnd > start);
            var expected = DomainLinks(dashboard[start..tableEnd]);
            var expanded = await client.GetStringAsync($"/domains?group=all&queue={queue}{categories}");
            Assert.Equal(expected, DomainLinks(expanded));
            Assert.Contains("Clear dashboard queue", expanded);
            Assert.DoesNotContain(">cobalt-freight.example</a>", expanded);
        }

        static string[] DomainLinks(string html) => System.Text.RegularExpressions.Regex.Matches(html, "<a[^>]*data-domain-link[^>]*>([^<]+)</a>")
            .Select(match => WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(match.Value, "href=\"/domains/([^\"/]+)").Groups[1].Value))
            .Distinct().Order().ToArray();
    }

    [Fact]
    public async Task GroupsDisplayInheritedAndExplicitPolicyTargets()
    {
        using var client = await SignInAsync("admin");
        var factory = _factory.Services.GetRequiredService<IDbContextFactory<DotMarcDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var settings = await DotMarc.Notifications.DnsRecordSettingsService.GetAsync(db);
        settings.TargetDmarcPolicy = DmarcPolicyLevel.Reject;
        var group = await db.Groups.SingleAsync(group => group.Name == "Aurora Retail");
        group.TargetDmarcPolicy = null;
        await db.SaveChangesAsync();
        Assert.Contains("DMARC policy target: Inherited (Reject)", await client.GetStringAsync("/groups"));
        group.TargetDmarcPolicy = DmarcPolicyLevel.Quarantine;
        group.TargetDmarcSubdomainPolicy = DmarcPolicyLevel.Reject;
        await db.SaveChangesAsync();
        Assert.Contains("DMARC policy target: Quarantine; subdomains: Reject", await client.GetStringAsync("/groups"));
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
        Assert.Contains("Review selected DNS", await client.GetStringAsync("/domains"));
        Assert.Contains("Recheck selected providers", await client.GetStringAsync("/domains"));
        Assert.DoesNotContain("Review selected DNS", dashboard);
        Assert.DoesNotContain("Recheck selected providers", dashboard);
    }

    [Theory]
    [InlineData("admin", true)]
    [InlineData("viewer", false)]
    public async Task BatchReviewOnlyShowsStoredRecordsToItsAuthorizedOwner(string persona, bool allowed)
    {
        using var client = await SignInAsync(persona);
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<DotMarcDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var domain = await db.Domains.SingleAsync(domain => domain.Name == "shop.aurora-retail.example");
        var batch = new DnsPushBatch
        {
            Id = Guid.NewGuid(), StartedBy = DashboardPreferencesStore.UserKeyOf(DotMarc.Audit.AuditActor.ForUser(null, DemoDataSeeder.AdminEmail, "Admin"))!,
            Provider = "cloudflare", Target = "dmarc", CreatedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30),
            Domains = [new DnsPushBatchDomain
            {
                DomainId = domain.Id, DomainName = domain.Name, Included = true, Status = DnsPushBatchDomainStatus.Planned,
                ReviewJson = System.Text.Json.JsonSerializer.Serialize(new DnsPushReview(domain.Name, [new("TXT", $"_dmarc.{domain.Name}", "v=DMARC1; p=reject; rua=mailto:review@example.com")], [], []))
            }]
        };
        db.DnsPushBatches.Add(batch);
        await db.SaveChangesAsync();
        var html = await client.GetStringAsync($"/dns-push/batches/{batch.Id}");
        Assert.Equal(allowed, html.Contains("review@example.com", StringComparison.Ordinal));
        Assert.Equal(allowed, html.Contains("Authorize and push checked domains", StringComparison.Ordinal));
        if (!allowed) Assert.Contains("unavailable or belongs to another person", html);
    }

    [Fact]
    public async Task BatchStartAndCallbackRejectDomainsOutsideTheCurrentScope()
    {
        using var client = await SignInAsync("viewer");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<DotMarcDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var viewer = await db.UserAccesses.Include(user => user.Role).SingleAsync(user => user.Email == DemoDataSeeder.ViewerEmail);
        viewer.Role.Permissions.Add(Permission.DomainsEdit);
        var outside = await db.Domains.SingleAsync(domain => domain.Name == "cobalt-freight.example");
        var batch = new DnsPushBatch
        {
            Id = Guid.NewGuid(), StartedBy = DashboardPreferencesStore.UserKeyOf(DotMarc.Audit.AuditActor.ForUser(null, DemoDataSeeder.ViewerEmail, "Viewer"))!,
            Provider = "cloudflare", Target = "dmarc", CreatedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30),
            Domains = [new() { DomainId = outside.Id, DomainName = outside.Name, Included = true, Status = DnsPushBatchDomainStatus.Planned }]
        };
        db.DnsPushBatches.Add(batch);
        await db.SaveChangesAsync();
        var denied = await client.GetAsync($"/dns-push/cloudflare/start-batch?batchId={batch.Id}");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Equal("/AccessDenied", denied.Headers.Location!.AbsolutePath);
        var protector = _factory.Services.GetRequiredService<DnsPushStateProtector>();
        var state = protector.Protect(0, "dmarc", "verifier", DateTimeOffset.UtcNow, batchId: batch.Id);
        Assert.Contains("Push not started", await client.GetStringAsync($"/dns-push/cloudflare/callback?code=test-code&state={Uri.EscapeDataString(state)}"));
        Assert.Equal(0, _dnsPush.ExchangeCalls);
        Assert.Empty(_dnsPush.Pushes);
    }

    [Fact]
    public async Task BaselineTabsExplainDefaultExceptionAndReviewFlow()
    {
        using var client = await SignInAsync("admin");
        var domain = await client.GetStringAsync("/baselines/domain");
        Assert.Contains("Saving a target changes", domain);
        Assert.Contains("Inherits system default", domain);
        Assert.Contains("Edit default target", domain);
        Assert.Contains("Set the defaults", domain);
        var defaults = domain[domain.IndexOf("System-wide defaults", StringComparison.Ordinal)..domain.IndexOf("Add exceptions only where needed", StringComparison.Ordinal)];
        Assert.Contains("Primary report address (configuration)", defaults);
        Assert.Contains("Secondary DMARC report address", defaults);
        Assert.Contains("DMARC policy target", defaults);
        Assert.DoesNotContain("<details", defaults);
        Assert.Contains("Set override", domain);
        Assert.Contains("Choose a domain in Domains", domain);
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
