using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Portal;
using Xunit;

namespace DotMarc.Tests.Portal;

public sealed class PortalStatusTests
{
    private static Domain HealthyDomain(Action<Domain>? change = null)
    {
        var domain = new Domain
        {
            Name = "aurora-retail.example", FirstSeenUtc = DateTimeOffset.UtcNow,
            DmarcCheckStatus = DmarcCheckStatus.Ok, DmarcPolicy = DmarcPolicyLevel.Reject, DmarcPercent = 100,
            DmarcAuthorizationCheckStatus = DmarcAuthorizationCheckStatus.Ok, SpfCheckStatus = SpfCheckStatus.Ok,
            MxCheckStatus = MxCheckStatus.Ok, DkimCheckStatus = DkimCheckStatus.Ok, TlsrptCheckStatus = TlsrptCheckStatus.Ok,
        };
        change?.Invoke(domain);
        return domain;
    }

    private static AlertEvent OpenAlert(string title) => new() { DomainName = "aurora-retail.example", AlertType = "MissedReport", Severity = "Warning", Title = title, Message = "m" };

    [Fact]
    public void AllChecksPassing_WithAnEnforcingPolicy_IsProtected_WithNoReasons()
    {
        var status = PortalStatus.For(HealthyDomain(), hasReports: true, []);

        Assert.Equal(PortalHealth.Protected, status.Health);
        Assert.Empty(status.Reasons);
    }

    [Theory]
    [InlineData(DmarcPolicyLevel.Quarantine, PortalHealth.Protected)]
    [InlineData(DmarcPolicyLevel.None, PortalHealth.MonitoringOnly)]
    public void ThePolicyDecidesBetweenProtectedAndMonitoringOnly(DmarcPolicyLevel policy, PortalHealth expected)
    {
        Assert.Equal(expected, PortalStatus.For(HealthyDomain(domain => domain.DmarcPolicy = policy), hasReports: true, []).Health);
    }

    [Fact]
    public void MonitoringOnly_ExplainsWhatThatMeans()
    {
        var status = PortalStatus.For(HealthyDomain(domain => domain.DmarcPolicy = DmarcPolicyLevel.None), hasReports: true, []);

        Assert.Equal(["The DMARC policy only monitors, so mail spoofing this domain isn't blocked yet."], status.Reasons);
    }

    [Fact]
    public void AFailingCheck_NeedsAttention_AndSaysWhich()
    {
        var status = PortalStatus.For(HealthyDomain(domain => domain.SpfCheckStatus = SpfCheckStatus.MissingRecord), hasReports: true, []);

        Assert.Equal(PortalHealth.NeedsAttention, status.Health);
        Assert.Contains(status.Reasons, reason => reason.StartsWith("SPF:", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOpenAlert_NeedsAttention_AndIsListed()
    {
        var status = PortalStatus.For(HealthyDomain(), hasReports: true, [OpenAlert("Missing expected DMARC report")]);

        Assert.Equal(PortalHealth.NeedsAttention, status.Health);
        Assert.Contains("Missing expected DMARC report", status.Reasons);
    }

    [Fact]
    public void NoReports_AndNothingElseWrong_IsNoReportsYet()
    {
        var status = PortalStatus.For(HealthyDomain(), hasReports: false, []);

        Assert.Equal(PortalHealth.NoReportsYet, status.Health);
        Assert.Equal(["No DMARC reports have arrived yet. They usually start within a few days."], status.Reasons);
    }

    [Fact]
    public void ChecksNotRunYet_OrNotConfigured_DontNeedAttention()
    {
        var domain = HealthyDomain(candidate =>
        {
            candidate.SpfCheckStatus = SpfCheckStatus.NotChecked;
            candidate.DkimCheckStatus = DkimCheckStatus.NotConfigured;
            candidate.DmarcAuthorizationCheckStatus = DmarcAuthorizationCheckStatus.NotApplicable;
        });

        Assert.Equal(PortalHealth.Protected, PortalStatus.For(domain, hasReports: true, []).Health);
    }

    [Fact]
    public void AFailedMtaStsHosting_NeedsAttention_ButNotHostingDoesnt()
    {
        Assert.Equal(PortalHealth.NeedsAttention, PortalStatus.For(HealthyDomain(domain => domain.MtaStsStatus = MtaStsStatus.Failed), true, []).Health);
        Assert.Equal(PortalHealth.Protected, PortalStatus.For(HealthyDomain(domain => domain.MtaStsStatus = MtaStsStatus.NotConfigured), true, []).Health);
    }

    [Theory]
    [InlineData(new[] { PortalHealth.Protected, PortalHealth.Protected, PortalHealth.Protected }, "All 3 domains are protected.")]
    [InlineData(new[] { PortalHealth.Protected }, "Your domain is protected.")]
    [InlineData(new[] { PortalHealth.Protected, PortalHealth.Protected, PortalHealth.NeedsAttention }, "2 of 3 domains are fully protected. 1 needs attention.")]
    [InlineData(new[] { PortalHealth.MonitoringOnly, PortalHealth.NeedsAttention }, "0 of 2 domains are fully protected. 1 needs attention.")]
    [InlineData(new[] { PortalHealth.Protected, PortalHealth.NeedsAttention, PortalHealth.NeedsAttention }, "1 of 3 domains are fully protected. 2 need attention.")]
    [InlineData(new[] { PortalHealth.MonitoringOnly }, "Your domain isn't fully protected yet.")]
    [InlineData(new[] { PortalHealth.NeedsAttention }, "Your domain needs attention.")]
    public void TheVerdict_SumsUpTheDomains(PortalHealth[] healths, string expected)
    {
        Assert.Equal(expected, PortalStatus.Verdict(healths.Select(health => new PortalDomainStatus(health, [])).ToList()));
    }

    [Fact]
    public void TheVerdict_ForNoDomains_SaysSo()
    {
        Assert.Equal("There are no domains to show yet.", PortalStatus.Verdict([]));
    }
}
