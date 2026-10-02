using DotMarc.Data;
using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class DnsHealthAlertEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static Domain HealthyDomain() => new()
    {
        Id = 7,
        Name = "contoso.com",
        IsMonitored = true,
        FirstSeenUtc = Now.AddDays(-30),
        DmarcCheckStatus = DmarcCheckStatus.Ok, DmarcCheckedUtc = Now.AddHours(-1),
        DmarcAuthorizationCheckStatus = DmarcAuthorizationCheckStatus.Ok, DmarcAuthorizationCheckedUtc = Now.AddHours(-1),
        TlsrptCheckStatus = TlsrptCheckStatus.Ok, TlsrptCheckedUtc = Now.AddHours(-1),
        SpfCheckStatus = SpfCheckStatus.Ok, SpfCheckedUtc = Now.AddHours(-1),
        MxCheckStatus = MxCheckStatus.Ok, MxCheckedUtc = Now.AddHours(-1),
        DkimSelectors = ["selector1"], DkimCheckStatus = DkimCheckStatus.Ok, DkimCheckedUtc = Now.AddHours(-1),
        MtaStsEnabled = true, MtaStsStatus = MtaStsStatus.Active, MtaStsCheckedUtc = Now.AddHours(-1),
        DmarcPolicy = DmarcPolicyLevel.Reject, DmarcSubdomainPolicy = DmarcPolicyLevel.Reject, DmarcPercent = 100,
        DnsNameservers = ["ns1.example.net", "ns2.example.net"], DnsProviderCheckedUtc = Now.AddHours(-1),
    };

    private static List<DnsHealthAlertAction> Run(Domain domain, List<DomainAlertState> states, NotificationSettings? settings = null, DateTimeOffset? now = null) =>
        DnsHealthAlertEvaluator.Evaluate(domain, states, settings ?? new NotificationSettings(), now ?? Now).ToList();

    private static IEnumerable<string> Raised(IEnumerable<DnsHealthAlertAction> actions) =>
        actions.Where(action => action.Kind == DnsHealthActionKind.Raise).Select(action => action.AlertType);

    private static bool Resolves(IEnumerable<DnsHealthAlertAction> actions, string alertType) =>
        actions.Any(action => action.Kind == DnsHealthActionKind.Resolve && action.AlertType == alertType);

    private static DomainAlertState State(List<DomainAlertState> states, string item) => states.Single(state => state.Item == item);

    /// <summary>States after a first cycle on a healthy domain: every check has passed, baselines are set.</summary>
    private static List<DomainAlertState> StatesAfterAHealthyCycle()
    {
        var states = new List<DomainAlertState>();
        Run(HealthyDomain(), states);
        return states;
    }

    private static Domain WithStatus(string item, string status)
    {
        var domain = HealthyDomain();
        switch (item)
        {
            case DnsHealthItems.Dmarc: domain.DmarcCheckStatus = Enum.Parse<DmarcCheckStatus>(status); break;
            case DnsHealthItems.DmarcAuthorization: domain.DmarcAuthorizationCheckStatus = Enum.Parse<DmarcAuthorizationCheckStatus>(status); break;
            case DnsHealthItems.Tlsrpt: domain.TlsrptCheckStatus = Enum.Parse<TlsrptCheckStatus>(status); break;
            case DnsHealthItems.Spf: domain.SpfCheckStatus = Enum.Parse<SpfCheckStatus>(status); break;
            case DnsHealthItems.Mx: domain.MxCheckStatus = Enum.Parse<MxCheckStatus>(status); break;
            case DnsHealthItems.Dkim: domain.DkimCheckStatus = Enum.Parse<DkimCheckStatus>(status); break;
            case DnsHealthItems.MtaSts: domain.MtaStsStatus = Enum.Parse<MtaStsStatus>(status); break;
        }

        return domain;
    }

    [Theory]
    [InlineData(DnsHealthItems.Dmarc, "Ok", DnsCheckHealth.Passing)]
    [InlineData(DnsHealthItems.Dmarc, "MissingOwnRecord", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Dmarc, "Misconfigured", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Dmarc, "MissingAuthorizationRecord", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Dmarc, "NotChecked", DnsCheckHealth.Ignored)]
    [InlineData(DnsHealthItems.DmarcAuthorization, "Ok", DnsCheckHealth.Passing)]
    [InlineData(DnsHealthItems.DmarcAuthorization, "Missing", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.DmarcAuthorization, "NotChecked", DnsCheckHealth.Ignored)]
    [InlineData(DnsHealthItems.DmarcAuthorization, "NotApplicable", DnsCheckHealth.Ignored)]
    [InlineData(DnsHealthItems.Tlsrpt, "Ok", DnsCheckHealth.Passing)]
    [InlineData(DnsHealthItems.Tlsrpt, "MissingOwnRecord", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Tlsrpt, "Misconfigured", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Tlsrpt, "NotChecked", DnsCheckHealth.Ignored)]
    [InlineData(DnsHealthItems.Spf, "Ok", DnsCheckHealth.Passing)]
    [InlineData(DnsHealthItems.Spf, "NullSpf", DnsCheckHealth.Passing)]
    [InlineData(DnsHealthItems.Spf, "MissingRecord", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Spf, "MultipleRecords", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Spf, "Misconfigured", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Spf, "NotChecked", DnsCheckHealth.Ignored)]
    [InlineData(DnsHealthItems.Mx, "Ok", DnsCheckHealth.Passing)]
    [InlineData(DnsHealthItems.Mx, "NullMx", DnsCheckHealth.Passing)]
    [InlineData(DnsHealthItems.Mx, "MissingRecord", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Mx, "UnresolvableTarget", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Mx, "NotChecked", DnsCheckHealth.Ignored)]
    [InlineData(DnsHealthItems.Dkim, "Ok", DnsCheckHealth.Passing)]
    [InlineData(DnsHealthItems.Dkim, "Missing", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Dkim, "Misconfigured", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Dkim, "NotConfigured", DnsCheckHealth.Ignored)]
    [InlineData(DnsHealthItems.MtaSts, "Active", DnsCheckHealth.Passing)]
    [InlineData(DnsHealthItems.MtaSts, "Failed", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.MtaSts, "PendingDns", DnsCheckHealth.Ignored)]
    [InlineData(DnsHealthItems.MtaSts, "PendingCertificate", DnsCheckHealth.Ignored)]
    [InlineData(DnsHealthItems.MtaSts, "NotConfigured", DnsCheckHealth.Ignored)]
    public void EachStatus_IsPassingFailingOrIgnored(string item, string status, DnsCheckHealth expected)
    {
        Assert.Equal(expected, DnsHealthChecks.For(item).Health(WithStatus(item, status)));
    }

    [Fact]
    public void MtaStsTurnedOff_IsIgnoredWhateverItsStatus()
    {
        var domain = WithStatus(DnsHealthItems.MtaSts, "Failed");
        domain.MtaStsEnabled = false;

        Assert.Equal(DnsCheckHealth.Ignored, DnsHealthChecks.For(DnsHealthItems.MtaSts).Health(domain));
    }

    [Fact]
    public void FirstRun_OnAHealthyDomain_RaisesNothing_AndRemembersEverything()
    {
        var states = new List<DomainAlertState>();

        var actions = Run(HealthyDomain(), states);

        Assert.Empty(Raised(actions));
        Assert.Equal(9, states.Count);
        Assert.All(states, state => Assert.Equal(7, state.DomainId));
        Assert.All(DnsHealthChecks.All, check => Assert.True(State(states, check.Item).HasPassed));
        Assert.Equal("p=reject; sp=reject; pct=100", State(states, DnsHealthItems.DmarcPolicy).Baseline);
        Assert.Equal("ns1.example.net;ns2.example.net", State(states, DnsHealthItems.Nameservers).Baseline);
    }

    [Fact]
    public void FirstRun_OnACheckThatHasNeverPassed_RaisesNothing_InWhenItBreaksMode()
    {
        var states = new List<DomainAlertState>();
        var domain = WithStatus(DnsHealthItems.Spf, "MissingRecord");

        var actions = Run(domain, states);
        var later = Run(domain, states, now: Now.AddHours(1));

        Assert.Empty(Raised(actions.Concat(later)));
        Assert.False(State(states, DnsHealthItems.Spf).HasPassed);
        Assert.Null(State(states, DnsHealthItems.Spf).PendingSinceUtc);
    }

    [Fact]
    public void ACheckThatBreaks_IsConfirmedByARecheckBeforeItAlerts()
    {
        var states = StatesAfterAHealthyCycle();
        var domain = WithStatus(DnsHealthItems.Spf, "MissingRecord");
        domain.SpfCheckedUtc = Now;
        domain.SpfCheckDetail = "No TXT record found at contoso.com";

        var firstFailure = Run(domain, states);
        Assert.Empty(Raised(firstFailure));
        Assert.Equal(Now, State(states, DnsHealthItems.Spf).PendingSinceUtc);
        Assert.Equal(Now.AddMinutes(15), State(states, DnsHealthItems.Spf).RecheckDueUtc);

        // Due, but the check hasn't run again yet.
        Assert.Empty(Raised(Run(domain, states, now: Now.AddMinutes(20))));

        domain.SpfCheckedUtc = Now.AddMinutes(17);
        var afterRecheck = Run(domain, states, now: Now.AddMinutes(20));

        var raised = Assert.Single(afterRecheck, action => action.Kind == DnsHealthActionKind.Raise);
        Assert.Equal(AlertTypes.SpfRecordBroken, raised.AlertType);
        Assert.Equal("Warning", raised.Severity);
        Assert.Equal("SPF record broken", raised.Title);
        Assert.Contains("contoso.com", raised.Message);
        Assert.Contains("missing record", raised.Message);
        Assert.Contains("No TXT record found at contoso.com", raised.Message);
        Assert.Contains("It was passing before.", raised.Message);
    }

    [Fact]
    public void ACheckThatPassesAgainBeforeItsRecheck_NeverAlerts_AndResolves()
    {
        var states = StatesAfterAHealthyCycle();
        var failing = WithStatus(DnsHealthItems.Mx, "MissingRecord");
        failing.MxCheckedUtc = Now;
        Run(failing, states);

        var recovered = HealthyDomain();
        recovered.MxCheckedUtc = Now.AddMinutes(17);
        var actions = Run(recovered, states, now: Now.AddMinutes(20));

        Assert.Empty(Raised(actions));
        Assert.True(Resolves(actions, AlertTypes.MxRecordBroken));
        Assert.Null(State(states, DnsHealthItems.Mx).PendingSinceUtc);
        Assert.Null(State(states, DnsHealthItems.Mx).RecheckDueUtc);
    }

    [Fact]
    public void AConfirmedFailure_KeepsBeingRaisedEachCycle_ForTheCooldownToDeduplicate()
    {
        var states = StatesAfterAHealthyCycle();
        var domain = WithStatus(DnsHealthItems.Tlsrpt, "MissingOwnRecord");
        domain.TlsrptCheckedUtc = Now;
        Run(domain, states);
        domain.TlsrptCheckedUtc = Now.AddMinutes(16);

        Assert.Contains(AlertTypes.TlsrptRecordBroken, Raised(Run(domain, states, now: Now.AddMinutes(20))));
        Assert.Contains(AlertTypes.TlsrptRecordBroken, Raised(Run(domain, states, now: Now.AddMinutes(25))));
    }

    [Fact]
    public void WheneverItFails_AlertsOnACheckThatHasNeverPassed()
    {
        var settings = new NotificationSettings { DkimAlertMode = DnsHealthAlertMode.WheneverItFails };
        var states = new List<DomainAlertState>();
        var domain = WithStatus(DnsHealthItems.Dkim, "Missing");
        domain.DkimCheckedUtc = Now;

        Assert.Empty(Raised(Run(domain, states, settings)));
        domain.DkimCheckedUtc = Now.AddMinutes(16);
        var actions = Run(domain, states, settings, Now.AddMinutes(20));

        var raised = Assert.Single(actions, action => action.Kind == DnsHealthActionKind.Raise);
        Assert.Equal(AlertTypes.DkimRecordBroken, raised.AlertType);
        Assert.DoesNotContain("It was passing before.", raised.Message);
    }

    [Fact]
    public void SwitchingBackToWhenItBreaks_ResolvesTheAlertsOfChecksThatNeverPassed()
    {
        // Whenever it fails raised alerts for a domain that was never set up; switching back must quiet them.
        var states = new List<DomainAlertState>();
        var domain = WithStatus(DnsHealthItems.Spf, "MissingRecord");
        Run(domain, states, new NotificationSettings { SpfAlertMode = DnsHealthAlertMode.WheneverItFails });

        var actions = Run(domain, states, now: Now.AddMinutes(20));

        Assert.Empty(Raised(actions));
        Assert.True(Resolves(actions, AlertTypes.SpfRecordBroken));
        Assert.Null(State(states, DnsHealthItems.Spf).PendingSinceUtc);
    }

    [Fact]
    public void Off_ResolvesTheAlertAndClearsThePendingFailure()
    {
        var states = StatesAfterAHealthyCycle();
        var domain = WithStatus(DnsHealthItems.Dmarc, "MissingOwnRecord");
        Run(domain, states);

        var actions = Run(domain, states, new NotificationSettings { DmarcAlertMode = DnsHealthAlertMode.Off }, Now.AddMinutes(20));

        Assert.Empty(Raised(actions));
        Assert.True(Resolves(actions, AlertTypes.DmarcRecordBroken));
        Assert.Null(State(states, DnsHealthItems.Dmarc).PendingSinceUtc);
    }

    [Fact]
    public void AnIgnoredStatus_ResolvesTheAlertAndClearsThePendingFailure()
    {
        var states = StatesAfterAHealthyCycle();
        Run(WithStatus(DnsHealthItems.Dkim, "Missing"), states);

        // The selectors were removed: the check is no longer configured.
        var actions = Run(WithStatus(DnsHealthItems.Dkim, "NotConfigured"), states, now: Now.AddMinutes(20));

        Assert.True(Resolves(actions, AlertTypes.DkimRecordBroken));
        Assert.Null(State(states, DnsHealthItems.Dkim).PendingSinceUtc);
    }

    [Fact]
    public void AStrongerPolicy_BecomesTheBaseline_AndResolves()
    {
        var states = new List<DomainAlertState> { new() { DomainId = 7, Item = DnsHealthItems.DmarcPolicy, Baseline = "p=quarantine; sp=quarantine; pct=100" } };

        var actions = Run(HealthyDomain(), states);

        Assert.Equal("p=reject; sp=reject; pct=100", State(states, DnsHealthItems.DmarcPolicy).Baseline);
        Assert.True(Resolves(actions, AlertTypes.DmarcPolicyWeakened));
    }

    [Theory]
    [InlineData(DmarcPolicyLevel.Quarantine, DmarcPolicyLevel.Reject, 100, "p=quarantine; sp=reject; pct=100")]
    [InlineData(DmarcPolicyLevel.Reject, DmarcPolicyLevel.None, 100, "p=reject; sp=none; pct=100")]
    [InlineData(DmarcPolicyLevel.Reject, DmarcPolicyLevel.Reject, 25, "p=reject; sp=reject; pct=25")]
    public void AWeakerPolicy_IsConfirmedThenRaised(DmarcPolicyLevel policy, DmarcPolicyLevel subdomainPolicy, int percent, string expectedNow)
    {
        var states = StatesAfterAHealthyCycle();
        var domain = HealthyDomain();
        (domain.DmarcPolicy, domain.DmarcSubdomainPolicy, domain.DmarcPercent, domain.DmarcCheckedUtc) = (policy, subdomainPolicy, percent, Now);

        Assert.Empty(Raised(Run(domain, states)));
        domain.DmarcCheckedUtc = Now.AddMinutes(16);
        var actions = Run(domain, states, now: Now.AddMinutes(20));

        var raised = Assert.Single(actions, action => action.Kind == DnsHealthActionKind.Raise);
        Assert.Equal(AlertTypes.DmarcPolicyWeakened, raised.AlertType);
        Assert.Equal("Warning", raised.Severity);
        Assert.Contains("p=reject; sp=reject; pct=100", raised.Message);
        Assert.Contains(expectedNow, raised.Message);
        Assert.Equal("p=reject; sp=reject; pct=100", State(states, DnsHealthItems.DmarcPolicy).Baseline);
    }

    [Fact]
    public void ARemovedDmarcRecord_LeavesThePolicyAlertAlone()
    {
        var states = StatesAfterAHealthyCycle();
        var domain = HealthyDomain();
        (domain.DmarcPolicy, domain.DmarcSubdomainPolicy, domain.DmarcPercent) = (null, null, null);

        var actions = Run(domain, states);

        Assert.DoesNotContain(actions, action => action.AlertType == AlertTypes.DmarcPolicyWeakened);
        Assert.Equal("p=reject; sp=reject; pct=100", State(states, DnsHealthItems.DmarcPolicy).Baseline);
    }

    [Fact]
    public void ThePolicyAlertTurnedOff_Resolves_AndKeepsTheBaseline()
    {
        var states = StatesAfterAHealthyCycle();
        var domain = HealthyDomain();
        domain.DmarcPolicy = DmarcPolicyLevel.None;

        var actions = Run(domain, states, new NotificationSettings { DmarcPolicyWeakenedEnabled = false });

        Assert.True(Resolves(actions, AlertTypes.DmarcPolicyWeakened));
        Assert.Empty(Raised(actions));
        Assert.Equal("p=reject; sp=reject; pct=100", State(states, DnsHealthItems.DmarcPolicy).Baseline);
    }

    [Fact]
    public void NameserversInADifferentOrderOrCase_AreTheSame()
    {
        var states = StatesAfterAHealthyCycle();
        var domain = HealthyDomain();
        domain.DnsNameservers = ["NS2.Example.Net.", "ns1.example.net"];

        var actions = Run(domain, states);

        Assert.Empty(Raised(actions));
        Assert.Null(State(states, DnsHealthItems.Nameservers).PendingSinceUtc);
        Assert.True(Resolves(actions, AlertTypes.NameserversChanged));
    }

    [Fact]
    public void ChangedNameservers_AreConfirmedThenRaisedAsInfo_AndResolveWhenTheyChangeBack()
    {
        var states = StatesAfterAHealthyCycle();
        var domain = HealthyDomain();
        domain.DnsNameservers = ["anna.ns.cloudflare.com", "bob.ns.cloudflare.com"];
        domain.DnsProvider = DetectedDnsProvider.Cloudflare;
        domain.DnsProviderCheckedUtc = Now;

        Assert.Empty(Raised(Run(domain, states)));
        domain.DnsProviderCheckedUtc = Now.AddMinutes(16);
        var raised = Assert.Single(Run(domain, states, now: Now.AddMinutes(20)), action => action.Kind == DnsHealthActionKind.Raise);

        Assert.Equal(AlertTypes.NameserversChanged, raised.AlertType);
        Assert.Equal("Info", raised.Severity);
        Assert.Contains("ns1.example.net", raised.Message);
        Assert.Contains("anna.ns.cloudflare.com", raised.Message);
        Assert.Contains("Cloudflare", raised.Message);

        var changedBack = Run(HealthyDomain(), states, now: Now.AddMinutes(30));
        Assert.True(Resolves(changedBack, AlertTypes.NameserversChanged));
        Assert.Null(State(states, DnsHealthItems.Nameservers).PendingSinceUtc);
    }

    [Fact]
    public void NoNameserversDetected_LeavesTheBaselineAlone()
    {
        var states = StatesAfterAHealthyCycle();
        var domain = HealthyDomain();
        domain.DnsNameservers = [];

        var actions = Run(domain, states);

        Assert.DoesNotContain(actions, action => action.AlertType == AlertTypes.NameserversChanged);
        Assert.Equal("ns1.example.net;ns2.example.net", State(states, DnsHealthItems.Nameservers).Baseline);
    }

    [Fact]
    public void NameserverKey_IsLowerCaseSortedAndWithoutTrailingDots_OrNullWhenEmpty()
    {
        Assert.Equal("a.example;b.example", DnsHealthAlertEvaluator.NameserverKey(["B.example.", " a.example ", "b.example"]));
        Assert.Null(DnsHealthAlertEvaluator.NameserverKey([]));
    }
}
