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
