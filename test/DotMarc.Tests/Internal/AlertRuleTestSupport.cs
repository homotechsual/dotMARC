using System.Text.Json;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Tests.Internal;

/// <summary>Helpers for tests that need an alert rule in a particular state. Alerting reads rules, not the old settings, so a test
/// that used to change a setting between cycles changes the default rule instead.</summary>
internal static class AlertRuleTestSupport
{
    /// <summary>Applies a change to a kind's default rule, creating the default pack first if it does not exist yet.</summary>
    public static async Task EditDefaultRuleAsync(string connectionString, string kind, Action<AlertRule> change)
    {
        await using var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connectionString).Options);
        await AlertRuleSeeder.EnsureDefaultPackAsync(context, CancellationToken.None);
        var rule = await context.AlertRules.SingleAsync(candidate => candidate.DefaultKey == kind);
        change(rule);
        await context.SaveChangesAsync();
    }

    /// <summary>Sets one parameter on a rule, keeping the others it already has.</summary>
    public static void SetParameter(AlertRule rule, string key, string value)
    {
        var values = string.IsNullOrWhiteSpace(rule.ParametersJson)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(rule.ParametersJson)!;
        values[key] = value;
        rule.ParametersJson = JsonSerializer.Serialize(values);
    }
}
