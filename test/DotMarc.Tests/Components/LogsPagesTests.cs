using System.Reflection;
using System.Security.Claims;
using DotMarc.Components.Pages;
using DotMarc.Data;
using DotMarc.ServerLogs;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Xunit;
using ServerLogsPage = DotMarc.Components.Pages.ServerLogs;

namespace DotMarc.Tests.Components;

/// <summary>The Logs pages share one tab list, and the old audit log address keeps working.</summary>
public sealed class LogsPagesTests
{
    private static ClaimsPrincipal User() => new(new ClaimsIdentity([], "Test"));

    private static List<string> Routes(Type page) =>
        page.GetCustomAttributes<RouteAttribute>().Select(route => route.Template).ToList();

    [Fact]
    public void ServerLogs_IsAtTheServerTab_AndRequiresLogsView()
    {
        Assert.Equal(["/logs/server"], Routes(typeof(ServerLogsPage)));
        var authorize = Assert.Single(typeof(ServerLogsPage).GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(nameof(Permission.LogsView), authorize.Policy);
    }

    [Fact]
    public void AuditLogPage_IsAtTheAuditTab_AndRequiresAuditView()
    {
        Assert.Equal(["/logs/audit"], Routes(typeof(AuditLogPage)));
        var authorize = Assert.Single(typeof(AuditLogPage).GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(nameof(Permission.AuditView), authorize.Policy);
    }

    [Fact]
    public void LogsIndex_IsAtLogs() =>
        Assert.Equal(["/logs"], Routes(typeof(LogsIndex)));

    [Fact]
    public void LegacyRedirects_CoversTheOldAuditAddress() =>
        Assert.Contains("/audit", Routes(typeof(LegacyRedirects)));

    [Fact]
    public void LegacyRedirectTargets_SendsTheOldAuditAddressToTheAuditTab_KeepingTheQuery() =>
        Assert.Equal("/logs/audit?action=domain.updated",
            LegacyRedirectTargets.DestinationFor(new Uri("https://x/audit?action=domain.updated")));

    [Fact]
    public void LegacyRedirectTargets_LeavesTheAuditExportAlone() =>
        Assert.Null(LegacyRedirectTargets.DestinationFor(new Uri("https://x/audit/export?from=2026-01-01")));

    [Fact]
    public void ForGivesServerThenAudit_WithTheHrefsTheRoutesUse()
    {
        var tabs = LogsTabs.For(User(), canViewServerLogs: true, canViewAudit: true);

        Assert.Equal([("Server", "/logs/server", true), ("Audit", "/logs/audit", true)],
            tabs.Select(tab => (tab.Title, tab.Href, tab.Visible)));
    }

    [Theory]
    [InlineData(true, true, new[] { "Server", "Audit" })]
    [InlineData(true, false, new[] { "Server" })]
    [InlineData(false, true, new[] { "Audit" })]
    [InlineData(false, false, new string[0])]
    public void For_ShowsOnlyTheTabsThePersonCanUse(bool canViewServerLogs, bool canViewAudit, string[] expectedVisibleTitles)
    {
        var tabs = LogsTabs.For(User(), canViewServerLogs, canViewAudit);

        Assert.Equal(expectedVisibleTitles, tabs.Where(tab => tab.Visible).Select(tab => tab.Title));
    }

    [Theory]
    [InlineData(new[] { "LogsView", "AuditView" }, new[] { "Server", "Audit" })]
    [InlineData(new[] { "LogsView" }, new[] { "Server" })]
    [InlineData(new[] { "AuditView" }, new[] { "Audit" })]
    [InlineData(new string[0], new string[0])]
    public async Task ForAsync_ChecksTheLogsViewAndAuditViewPolicies(string[] grantedPolicies, string[] expectedVisibleTitles)
    {
        var tabs = await LogsTabs.ForAsync(User(), new PolicyNamedAuthorizationService(grantedPolicies));

        Assert.Equal(expectedVisibleTitles, tabs.Where(tab => tab.Visible).Select(tab => tab.Title));
    }
}
