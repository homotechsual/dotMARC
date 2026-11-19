using System.Reflection;
using DotMarc.Components.Dialogs;
using DotMarc.Components.Pages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace DotMarc.Tests.Components;

/// <summary>The Alert rules page is where people with the permission change rules, and where everyone who can see alerts reads them.</summary>
public sealed class AlertRulesPageTests
{
    [Fact]
    public void ThePage_IsAtAlertsRules_AndRequiresTheAlertsViewPolicy()
    {
        var route = typeof(AlertRules).GetCustomAttribute<RouteAttribute>();
        var authorize = typeof(AlertRules).GetCustomAttribute<AuthorizeAttribute>();

        Assert.Equal("/alerts/rules", route?.Template);
        Assert.Equal("AlertsView", authorize?.Policy);
    }

    [Fact]
    public void TheDialog_IsNotARoutablePage()
    {
        Assert.Null(typeof(AlertRuleDialog).GetCustomAttribute<RouteAttribute>());
    }

    [Fact]
    public void ThePageAndTheDialog_AskForTheKindAndRuleByParameter()
    {
        var parameters = typeof(AlertRuleDialog).GetProperties()
            .Where(property => property.GetCustomAttribute<ParameterAttribute>() is not null)
            .Select(property => property.Name)
            .Order()
            .ToList();

        Assert.Equal(["Kind", "RuleId"], parameters);
    }
}
