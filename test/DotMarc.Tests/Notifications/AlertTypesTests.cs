// test/DotMarc.Tests/Notifications/AlertTypesTests.cs
using System.Reflection;
using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class AlertTypesTests
{
    [Fact]
    public void EveryKeyConstant_IsInTheRegistry_SoANewAlertTypeCannotBeMissingFromTheUi()
    {
        var constantValues = typeof(AlertTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(constantValues);
        Assert.Equal(constantValues.Order(), AlertTypes.All.Select(alertType => alertType.Key).Order());
    }

    [Fact]
    public void Find_ReturnsTheAlertType_OrNullForAnUnknownKey()
    {
        Assert.Equal("TlsrptFailure", AlertTypes.Find("TlsrptFailure")!.Key);
        Assert.Null(AlertTypes.Find("NotARealAlertType"));
    }

    [Fact]
    public void All_ListsTheAlertTypesDotMarcRaises()
    {
        Assert.Equal(
            ["MissedReport", "SuspiciousRejectActivity", "TlsrptFailure", "UnexpectedActivityOnNullRoutedDomain",
             "DmarcRecordBroken", "DmarcAuthorizationBroken", "TlsrptRecordBroken", "SpfRecordBroken", "MxRecordBroken",
             "DkimRecordBroken", "MtaStsFailing", "DmarcPolicyWeakened", "NameserversChanged", "ApiKeyExpiring", "ClientReportFailed", "PsaConnectionLost",
             "NewUnreviewedSender", "UnauthorisedSenderDelivered", "PassRateLow", "VolumeChanged", "RejectedMessagesHigh"],
            AlertTypes.All.Select(alertType => alertType.Key));
    }

    [Fact]
    public void EveryAlertType_HasANameAndADescription_AndOnlyTheQuietOnesSkipTicketsByDefault()
    {
        foreach (var alertType in AlertTypes.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(alertType.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(alertType.Description));
            Assert.Equal(alertType.Key is not (AlertTypes.NameserversChanged or AlertTypes.ApiKeyExpiring or AlertTypes.ClientReportFailed or AlertTypes.PsaConnectionLost or AlertTypes.NewUnreviewedSender
                or AlertTypes.PassRateLow or AlertTypes.VolumeChanged or AlertTypes.RejectedMessagesHigh), alertType.CreatesTicketByDefault);
        }
    }

    [Fact]
    public void TheSenderAlertTypes_AreRegistered_WithTheirTicketDefaults()
    {
        Assert.False(AlertTypes.Find(AlertTypes.NewUnreviewedSender)!.CreatesTicketByDefault);
        Assert.True(AlertTypes.Find(AlertTypes.UnauthorisedSenderDelivered)!.CreatesTicketByDefault);
        Assert.Equal("NewUnreviewedSender", AlertTypes.NewUnreviewedSender);
        Assert.Equal("UnauthorisedSenderDelivered", AlertTypes.UnauthorisedSenderDelivered);
    }

    [Fact]
    public void DnsHealth_ListsTheNineDnsHealthAlertTypes()
    {
        Assert.Equal(9, AlertTypes.DnsHealth.Count);
        Assert.All(AlertTypes.DnsHealth, key => Assert.NotNull(AlertTypes.Find(key)));
    }
}
