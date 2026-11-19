using DotMarc.Data;
using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>DnsHealthAlertEvaluator with rules: per-rule modes and severities, scope, stacking and the shared confirmation delay.</summary>
public sealed class DnsHealthRuleEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private const int AuroraGroupId = 4;

    private static Domain DomainWithBrokenSpf(bool inAurora)
    {
        var domain = new Domain
        {
            Id = 7, Name = "contoso.com", IsMonitored = true, FirstSeenUtc = Now.AddDays(-30),
            SpfCheckStatus = SpfCheckStatus.MissingRecord, SpfCheckedUtc = Now,
            DmarcPolicy = DmarcPolicyLevel.Reject, DmarcSubdomainPolicy = DmarcPolicyLevel.Reject, DmarcPercent = 100,
            DmarcCheckStatus = DmarcCheckStatus.Ok, DmarcCheckedUtc = Now.AddHours(-1),
            DnsNameservers = ["ns1.example.net"], DnsProviderCheckedUtc = Now.AddHours(-1)
        };
        if (inAurora)
        {
            domain.Groups.Add(new Group { Id = AuroraGroupId, Name = "Aurora" });
        }

        return domain;
    }

    /// <summary>The default pack with ids 101 and up, plus the given rules, which sit above it.</summary>
    private static AlertRuleSet Rules(NotificationSettings settings, params AlertRule[] extraRules)
    {
        var pack = AlertRuleSeeder.DefaultPack();
        for (var index = 0; index < pack.Count; index++)
        {
            pack[index].Id = 101 + index;
        }

        return new AlertRuleSet([.. pack, .. extraRules], settings);
    }

    private static AlertRule Extra(int id, string kind, int position = -10, bool stop = true, string? json = null, string severity = "Warning", params AlertRuleScopeEntry[] scope) => new()
    {
        Id = id, Kind = kind, Name = $"Extra {id}", Severity = severity, Position = position, StopProcessing = stop, ParametersJson = json, Scope = [.. scope]
    };

    private static AlertRuleScopeEntry InAuroraGroup() => new() { Target = AlertRuleScopeTarget.Group, TargetId = AuroraGroupId };

    private static List<DnsHealthAlertAction> Run(Domain domain, List<DomainAlertState> states, AlertRuleSet rules, DateTimeOffset now) =>
        DnsHealthAlertEvaluator.Evaluate(domain, states, rules, new NotificationSettings(), now).ToList();

    /// <summary>Two cycles 20 minutes apart with a recheck in between, which is when a pending failure is confirmed.</summary>
    private static List<DnsHealthAlertAction> RunToConfirmation(Domain domain, AlertRuleSet rules, List<DomainAlertState>? states = null)
    {
        states ??= [];
        Run(domain, states, rules, Now);
        domain.SpfCheckedUtc = Now.AddMinutes(16);
        return Run(domain, states, rules, Now.AddMinutes(20));
    }

    [Fact]
    public void ARuleWithModeWheneverItFails_AlertsForADomainThatNeverPassed_WhileTheDefaultStaysQuiet()
    {
        var rules = Rules(new NotificationSettings(), Extra(1, AlertTypes.SpfRecordBroken, json: "{\"mode\":\"WheneverItFails\"}", scope: InAuroraGroup()));

        var inGroup = RunToConfirmation(DomainWithBrokenSpf(inAurora: true), rules);
        var outsideGroup = RunToConfirmation(DomainWithBrokenSpf(inAurora: false), rules);

        var raised = Assert.Single(inGroup, action => action.Kind == DnsHealthActionKind.Raise);
        Assert.Equal(1, raised.RuleId);
        Assert.DoesNotContain(outsideGroup, action => action.Kind == DnsHealthActionKind.Raise);
    }

    [Fact]
    public void StackedRules_WithDifferentModes_RaiseOnlyForTheOneThatWantsIt_AndResolveTheOther()
    {
        var rules = Rules(new NotificationSettings(),
            Extra(1, AlertTypes.SpfRecordBroken, position: -20, stop: false),
            Extra(2, AlertTypes.SpfRecordBroken, position: -10, stop: true, json: "{\"mode\":\"WheneverItFails\"}"));

        var actions = RunToConfirmation(DomainWithBrokenSpf(inAurora: false), rules);

        var raised = Assert.Single(actions, action => action.Kind == DnsHealthActionKind.Raise);
        Assert.Equal(2, raised.RuleId);
        Assert.Contains(actions, action => action.Kind == DnsHealthActionKind.Resolve && action.AlertType == AlertTypes.SpfRecordBroken && action.RuleId == 1);
    }

    [Fact]
    public void TheSharedConfirmationDelay_StillApplies_WithRules()
    {
        var rules = Rules(new NotificationSettings(), Extra(1, AlertTypes.SpfRecordBroken, json: "{\"mode\":\"WheneverItFails\"}"));
        var states = new List<DomainAlertState>();
        var domain = DomainWithBrokenSpf(inAurora: false);

        var first = Run(domain, states, rules, Now);

        Assert.DoesNotContain(first, action => action.Kind == DnsHealthActionKind.Raise);
        Assert.NotNull(states.Single(state => state.Item == DnsHealthItems.Spf).PendingSinceUtc);
    }

    [Fact]
    public void ARuleSeverity_IsUsed_ForThePolicyWeakenedAlert()
    {
        var rules = Rules(new NotificationSettings(), Extra(1, AlertTypes.DmarcPolicyWeakened, severity: "Info"));
        var states = new List<DomainAlertState>();
        var healthy = DomainWithBrokenSpf(inAurora: false);
        Run(healthy, states, rules, Now);

        var weakened = DomainWithBrokenSpf(inAurora: false);
        (weakened.DmarcPolicy, weakened.DmarcCheckedUtc) = (DmarcPolicyLevel.Quarantine, Now);
        Run(weakened, states, rules, Now.AddMinutes(1));
        weakened.DmarcCheckedUtc = Now.AddMinutes(30);
        var actions = Run(weakened, states, rules, Now.AddMinutes(40));

        var raised = Assert.Single(actions, action => action.Kind == DnsHealthActionKind.Raise && action.AlertType == AlertTypes.DmarcPolicyWeakened);
        Assert.Equal("Info", raised.Severity);
        Assert.Equal(1, raised.RuleId);
    }

    [Fact]
    public void ADisabledRule_LeavesNoApplicableRule_SoItsAlertsResolveAndPendingClears()
    {
        var disabledPack = Rules(new NotificationSettings());
        var spfRule = disabledPack.Find(101 + AlertRuleKinds.All.ToList().FindIndex(kind => kind.Key == AlertTypes.SpfRecordBroken))!;
        spfRule.Enabled = false;
        var states = new List<DomainAlertState> { new() { DomainId = 7, Item = DnsHealthItems.Spf, HasPassed = true, PendingSinceUtc = Now, RecheckDueUtc = Now.AddMinutes(15) } };

        var actions = Run(DomainWithBrokenSpf(inAurora: false), states, disabledPack, Now.AddMinutes(20));

        Assert.Contains(actions, action => action.Kind == DnsHealthActionKind.Resolve && action.AlertType == AlertTypes.SpfRecordBroken && action.RuleId is null);
        Assert.Null(states.Single(state => state.Item == DnsHealthItems.Spf).PendingSinceUtc);
    }

    [Fact]
    public void APolicyWeakenedRule_ScopedToAGroup_OnlyAlertsThatGroup()
    {
        // The default policy rule is switched off, so only the Aurora rule applies, and only to Aurora's domains.
        var rules = Rules(new NotificationSettings(), Extra(1, AlertTypes.DmarcPolicyWeakened, scope: InAuroraGroup()));
        var defaultPolicyRuleId = 101 + AlertRuleKinds.All.ToList().FindIndex(kind => kind.Key == AlertTypes.DmarcPolicyWeakened);
        rules.Find(defaultPolicyRuleId)!.Enabled = false;

        List<DnsHealthAlertAction> Weakened(bool inAurora)
        {
            var states = new List<DomainAlertState>();
            Run(DomainWithBrokenSpf(inAurora), states, rules, Now);
            var weakened = DomainWithBrokenSpf(inAurora);
            (weakened.DmarcPolicy, weakened.DmarcCheckedUtc) = (DmarcPolicyLevel.Quarantine, Now);
            Run(weakened, states, rules, Now.AddMinutes(1));
            weakened.DmarcCheckedUtc = Now.AddMinutes(30);
            return Run(weakened, states, rules, Now.AddMinutes(40));
        }

        Assert.Contains(Weakened(inAurora: true), action => action.Kind == DnsHealthActionKind.Raise && action.AlertType == AlertTypes.DmarcPolicyWeakened);
        Assert.DoesNotContain(Weakened(inAurora: false), action => action.Kind == DnsHealthActionKind.Raise && action.AlertType == AlertTypes.DmarcPolicyWeakened);
    }
}
