using DotMarc.Data;
using DotMarc.Portal;
using DotMarc.Reporting.ClientReports;
using Xunit;

namespace DotMarc.Tests.Reporting.ClientReports;

public sealed class ClientReportNextStepsTests
{
    private static Domain Healthy(string name = "aurora-retail.example", DmarcPolicyLevel policy = DmarcPolicyLevel.Reject) => new()
    {
        Name = name, IsMonitored = true, DmarcPolicy = policy,
        DmarcCheckStatus = DmarcCheckStatus.Ok, SpfCheckStatus = SpfCheckStatus.Ok, MxCheckStatus = MxCheckStatus.Ok,
        DkimCheckStatus = DkimCheckStatus.Ok, TlsrptCheckStatus = TlsrptCheckStatus.Ok,
    };

    private static ClientReportDomain Row(string name, long messages, params ClientReportSender[] senders) => new(
        name, new PortalDomainStatus(PortalHealth.Protected, []), messages, 1.0, null, [], "", [], senders, new ReceiverActions(messages, 0, 0, 0), []);

    [Fact]
    public void AGroupWithNoDomains_IsNotToldEveryDomainIsProtected()
    {
        Assert.Equal([ClientReportNextSteps.NoDomains], ClientReportNextSteps.For([], []));
    }

    [Fact]
    public void AnAllProtectedGroup_HasNothingToDo()
    {
        Assert.Equal([ClientReportNextSteps.NothingToDo], ClientReportNextSteps.For([Healthy()], [Row("aurora-retail.example", 100)]));
    }

    [Fact]
    public void AMonitoringOnlyPolicy_IsToldToMoveToQuarantine_AndQuarantineToReject()
    {
        var steps = ClientReportNextSteps.For(
            [Healthy("aurora-retail.example", DmarcPolicyLevel.None), Healthy("shop.aurora-retail.example", DmarcPolicyLevel.Quarantine)],
            [Row("aurora-retail.example", 100), Row("shop.aurora-retail.example", 100)]);

        Assert.Equal(
        [
            "Move aurora-retail.example from monitoring to a quarantine policy, so mail that fails DMARC is sent to spam.",
            "When you're ready, move shop.aurora-retail.example to a reject policy.",
        ], steps);
    }

    [Fact]
    public void ADomainWithNoPolicyFound_IsAlsoToldToMoveToQuarantine()
    {
        var domain = Healthy();
        domain.DmarcPolicy = null;

        Assert.Equal(["Move aurora-retail.example from monitoring to a quarantine policy, so mail that fails DMARC is sent to spam."],
            ClientReportNextSteps.For([domain], [Row("aurora-retail.example", 100)]));
    }

    [Fact]
    public void ADomainWithNoDmarcRecord_IsOnlyToldToPublishOne()
    {
        var domain = Healthy();
        domain.DmarcPolicy = null;
        domain.DmarcCheckStatus = DmarcCheckStatus.MissingOwnRecord;

        Assert.Equal(["Publish a DMARC record for aurora-retail.example."], ClientReportNextSteps.For([domain], [Row("aurora-retail.example", 100)]));
    }

    [Fact]
    public void AFailingCheck_GetsItsOwnSentence()
    {
        var domain = Healthy();
        domain.SpfCheckStatus = SpfCheckStatus.MissingRecord;

        Assert.Contains("Publish an SPF record for aurora-retail.example.", ClientReportNextSteps.For([domain], [Row("aurora-retail.example", 100)]));
    }

    [Fact]
    public void AKnownSenderFailingOver5Percent_IsNamed_ButNotAnUnknownOne()
    {
        var senders = new[]
        {
            new ClientReportSender("198.51.100.7", "Mailchimp", 60, 0, 60, 0.06),
            new ClientReportSender("198.51.100.8", null, 90, 0, 90, 0.09),
            new ClientReportSender("198.51.100.9", "Zendesk", 40, 0, 40, 0.04),
        };

        var steps = ClientReportNextSteps.For([Healthy()], [Row("aurora-retail.example", 1000, senders)]);

        Assert.Equal(["Mailchimp sent 60 messages as aurora-retail.example that failed DMARC. If they send for you, add them to SPF or set up DKIM for them."], steps);
    }

    [Fact]
    public void ADomainWithNoReports_IsToldToCheckItsReportingAddress()
    {
        Assert.Equal(["No DMARC reports arrived for aurora-retail.example. Check its DMARC record's reporting address."],
            ClientReportNextSteps.For([Healthy()], [Row("aurora-retail.example", 0)]));
    }
}
