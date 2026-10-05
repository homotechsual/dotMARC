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
             "DkimRecordBroken", "MtaStsFailing", "DmarcPolicyWeakened", "NameserversChanged", "ApiKeyExpiring"],
            AlertTypes.All.Select(alertType => alertType.Key));
    }

    [Fact]
    public void EveryAlertType_HasANameAndADescription_AndAllButNameserverChangesAndExpiringKeysCreateTickets()
    {
        foreach (var alertType in AlertTypes.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(alertType.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(alertType.Description));
            Assert.Equal(alertType.Key is not (AlertTypes.NameserversChanged or AlertTypes.ApiKeyExpiring), alertType.CreatesTicketByDefault);
        }
    }

    [Fact]
    public void DnsHealth_ListsTheNineDnsHealthAlertTypes()
    {
        Assert.Equal(9, AlertTypes.DnsHealth.Count);
        Assert.All(AlertTypes.DnsHealth, key => Assert.NotNull(AlertTypes.Find(key)));
    }
}
