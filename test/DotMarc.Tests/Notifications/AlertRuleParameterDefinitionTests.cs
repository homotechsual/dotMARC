using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class AlertRuleParameterDefinitionTests
{
    public static TheoryData<string, string, string, int?, int?> IntegerParameters => new()
    {
        { AlertTypes.MissedReport, AlertRuleParameterKeys.MissingDays, "3", 1, 90 },
        { AlertTypes.SuspiciousRejectActivity, AlertRuleParameterKeys.MinimumMessages, "10", 1, 1000000 },
        { AlertTypes.SuspiciousRejectActivity, AlertRuleParameterKeys.NonBenignPercent, "50", 1, 100 },
        { AlertTypes.NewUnreviewedSender, AlertRuleParameterKeys.NewDays, "7", 1, 30 },
        { AlertTypes.NewUnreviewedSender, AlertRuleParameterKeys.MinimumMessages, "20", 1, 1000000 },
        { AlertTypes.NewUnreviewedSender, AlertRuleParameterKeys.UnderReviewDays, "14", 1, 365 },
        { AlertTypes.UnauthorisedSenderDelivered, AlertRuleParameterKeys.MinimumMessages, "10", 1, 1000000 },
    };

    [Theory]
    [MemberData(nameof(IntegerParameters))]
    public void EachIntegerParameter_IsDefinedAsTheSpecSays(string kind, string key, string expectedDefault, int? min, int? max)
    {
        var definition = Assert.Single(AlertRuleKinds.Find(kind)!.Parameters, parameter => parameter.Key == key);

        Assert.Equal(AlertRuleParameterType.Integer, definition.Type);
        Assert.Equal((expectedDefault, min, max), (definition.Default, definition.Min, definition.Max));
        Assert.False(string.IsNullOrWhiteSpace(definition.Label));
        Assert.False(string.IsNullOrWhiteSpace(definition.Help));
    }

    [Fact]
    public void EachDnsRecordKind_HasAModeChoiceDefaultingToWhenItBreaks()
    {
        var dnsRecordKinds = new[]
        {
            AlertTypes.DmarcRecordBroken, AlertTypes.DmarcAuthorizationBroken, AlertTypes.TlsrptRecordBroken, AlertTypes.SpfRecordBroken,
            AlertTypes.MxRecordBroken, AlertTypes.DkimRecordBroken, AlertTypes.MtaStsFailing
        };

        foreach (var kind in dnsRecordKinds)
        {
            var mode = Assert.Single(AlertRuleKinds.Find(kind)!.Parameters);
            Assert.Equal((AlertRuleParameterKeys.Mode, AlertRuleParameterType.Choice, "WhenItBreaks"), (mode.Key, mode.Type, mode.Default));
            Assert.Equal(["WhenItBreaks", "WheneverItFails"], mode.Choices!.Select(choice => choice.Value));
        }
    }

    [Fact]
    public void UnderReview_IsAChoice_AndItsDaysShowOnlyForAfterDays()
    {
        var parameters = AlertRuleKinds.Find(AlertTypes.NewUnreviewedSender)!.Parameters;

        var underReview = parameters.Single(parameter => parameter.Key == AlertRuleParameterKeys.UnderReview);
        Assert.Equal(["decided", "unreviewed", "afterDays"], underReview.Choices!.Select(choice => choice.Value));
        Assert.Equal("decided", underReview.Default);
        var days = parameters.Single(parameter => parameter.Key == AlertRuleParameterKeys.UnderReviewDays);
        Assert.Equal((AlertRuleParameterKeys.UnderReview, "afterDays"), (days.ShowWhenKey, days.ShowWhenValue));
    }

    [Fact]
    public void KindsWithNoParameters_HaveAnEmptyList()
    {
        foreach (var kind in new[] { AlertTypes.TlsrptFailure, AlertTypes.UnexpectedActivityOnNullRoutedDomain, AlertTypes.DmarcPolicyWeakened, AlertTypes.NameserversChanged, AlertTypes.ApiKeyExpiring, AlertTypes.ClientReportFailed, AlertTypes.PsaConnectionLost })
        {
            Assert.Empty(AlertRuleKinds.Find(kind)!.Parameters);
        }
    }

    [Fact]
    public void OnlyThePolicyAndNameserverKinds_CanAutoClose()
    {
        var withAutoClose = AlertRuleKinds.All.Where(kind => kind.HasAutoClose).Select(kind => kind.Key).Order().ToList();

        Assert.Equal(new[] { AlertTypes.DmarcPolicyWeakened, AlertTypes.NameserversChanged }.Order().ToList(), withAutoClose);
    }

    [Fact]
    public void EveryDefaultIsInsideItsOwnRange_AndEveryChoiceDefaultIsAChoice()
    {
        foreach (var definition in AlertRuleKinds.All.SelectMany(kind => kind.Parameters))
        {
            if (definition.Type == AlertRuleParameterType.Integer)
            {
                var value = int.Parse(definition.Default);
                Assert.InRange(value, definition.Min!.Value, definition.Max!.Value);
            }
            else
            {
                Assert.Contains(definition.Default, definition.Choices!.Select(choice => choice.Value));
            }
        }
    }

    [Fact]
    public void DefaultOf_ReturnsTheDefault_AndThrowsForAnUnknownPair()
    {
        Assert.Equal("3", AlertRuleKinds.DefaultOf(AlertTypes.MissedReport, AlertRuleParameterKeys.MissingDays));
        Assert.Equal(3, AlertRuleKinds.DefaultInt(AlertTypes.MissedReport, AlertRuleParameterKeys.MissingDays));
        Assert.Throws<ArgumentException>(() => AlertRuleKinds.DefaultOf(AlertTypes.MissedReport, "nope"));
        Assert.Throws<ArgumentException>(() => AlertRuleKinds.DefaultOf("NoSuchKind", AlertRuleParameterKeys.MissingDays));
    }

    [Fact]
    public void TheDefinitionDefaults_MatchTheNotificationSettingsDefaults()
    {
        var settings = new NotificationSettings();

        Assert.Equal(settings.MissingReportThresholdDays, AlertRuleKinds.DefaultInt(AlertTypes.MissedReport, AlertRuleParameterKeys.MissingDays));
        Assert.Equal(settings.SuspiciousRejectMinVolume, AlertRuleKinds.DefaultInt(AlertTypes.SuspiciousRejectActivity, AlertRuleParameterKeys.MinimumMessages));
        Assert.Equal(settings.SuspiciousRejectNonBenignPercent, AlertRuleKinds.DefaultInt(AlertTypes.SuspiciousRejectActivity, AlertRuleParameterKeys.NonBenignPercent));
        Assert.Equal(settings.NewSenderDays, AlertRuleKinds.DefaultInt(AlertTypes.NewUnreviewedSender, AlertRuleParameterKeys.NewDays));
        Assert.Equal(settings.NewSenderMinVolume, AlertRuleKinds.DefaultInt(AlertTypes.NewUnreviewedSender, AlertRuleParameterKeys.MinimumMessages));
        Assert.Equal(settings.UnauthorisedDeliveredMinVolume, AlertRuleKinds.DefaultInt(AlertTypes.UnauthorisedSenderDelivered, AlertRuleParameterKeys.MinimumMessages));
        Assert.All(DnsHealthChecks.All, check =>
            Assert.Equal(check.Mode(settings).ToString(), AlertRuleKinds.DefaultOf(check.AlertType, AlertRuleParameterKeys.Mode)));
    }

    [Fact]
    public void Severities_AreInfoWarningCritical()
    {
        Assert.Equal(["Info", "Warning", "Critical"], AlertSeverities.All);
    }
}
