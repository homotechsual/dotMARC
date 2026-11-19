using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class AlertRuleKindsTests
{
    [Fact]
    public void EveryAlertType_HasAKind_InTheSameOrder()
    {
        Assert.Equal(AlertTypes.All.Select(type => type.Key), AlertRuleKinds.All.Select(kind => kind.Key));
    }

    [Fact]
    public void OnlyTheThreeSystemKinds_HaveNoDomainScope()
    {
        var withoutScope = AlertRuleKinds.All.Where(kind => !kind.HasDomainScope).Select(kind => kind.Key).Order().ToList();

        Assert.Equal(
            new[] { AlertTypes.ApiKeyExpiring, AlertTypes.ClientReportFailed, AlertTypes.PsaConnectionLost }.Order().ToList(),
            withoutScope);
    }

    [Fact]
    public void NameserversChanged_DefaultsToInfo_AndTheRestToWarning()
    {
        Assert.Equal("Info", AlertRuleKinds.Find(AlertTypes.NameserversChanged)!.DefaultSeverity);
        Assert.All(AlertRuleKinds.All.Where(kind => kind.Key != AlertTypes.NameserversChanged), kind => Assert.Equal("Warning", kind.DefaultSeverity));
    }

    [Theory]
    [InlineData(AlertTypes.NewUnreviewedSender, true)]
    [InlineData(AlertTypes.NewUnreviewedSender, false)]
    [InlineData(AlertTypes.UnauthorisedSenderDelivered, true)]
    [InlineData(AlertTypes.UnauthorisedSenderDelivered, false)]
    [InlineData(AlertTypes.DmarcPolicyWeakened, true)]
    [InlineData(AlertTypes.DmarcPolicyWeakened, false)]
    [InlineData(AlertTypes.NameserversChanged, true)]
    [InlineData(AlertTypes.NameserversChanged, false)]
    public void IsEnabledBySettings_FollowsEachLegacySwitch(string kind, bool switchedOn)
    {
        var settings = new NotificationSettings
        {
            NewSenderAlertEnabled = switchedOn,
            UnauthorisedDeliveredAlertEnabled = switchedOn,
            DmarcPolicyWeakenedEnabled = switchedOn,
            NameserversChangedEnabled = switchedOn
        };

        Assert.Equal(switchedOn, AlertRuleKinds.IsEnabledBySettings(kind, settings));
    }

    [Fact]
    public void ADnsRecordKind_IsOffWhenItsSettingModeIsOff()
    {
        var settings = new NotificationSettings { SpfAlertMode = DnsHealthAlertMode.Off };

        Assert.False(AlertRuleKinds.IsEnabledBySettings(AlertTypes.SpfRecordBroken, settings));
        Assert.True(AlertRuleKinds.IsEnabledBySettings(AlertTypes.DmarcRecordBroken, settings));
    }

    [Fact]
    public void OtherKinds_AreEnabledBySettings()
    {
        var settings = new NotificationSettings();

        Assert.True(AlertRuleKinds.IsEnabledBySettings(AlertTypes.MissedReport, settings));
        Assert.True(AlertRuleKinds.IsEnabledBySettings(AlertTypes.ApiKeyExpiring, settings));
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData("", 2)]
    [InlineData("{\"days\":\"5\"}", 5)]
    [InlineData("{\"days\":\"abc\"}", 2)]
    [InlineData("{\"other\":\"5\"}", 2)]
    [InlineData("{", 2)]
    public void Parameters_Int_ReadsAnOverride_AndFallsBackWhenUnsetOrBad(string? json, int expected)
    {
        Assert.Equal(expected, new AlertRuleParameters(json).Int(AlertRuleParameterKeys.MissingDays, fallback: 2));
    }

    [Theory]
    [InlineData("{\"mode\":\"WheneverItFails\"}", DnsHealthAlertMode.WheneverItFails)]
    [InlineData("{\"mode\":\"nope\"}", DnsHealthAlertMode.WhenItBreaks)]
    [InlineData(null, DnsHealthAlertMode.WhenItBreaks)]
    public void Parameters_Mode_ReadsAnEnumName_AndFallsBack(string? json, DnsHealthAlertMode expected)
    {
        Assert.Equal(expected, new AlertRuleParameters(json).Mode(DnsHealthAlertMode.WhenItBreaks));
    }
}
