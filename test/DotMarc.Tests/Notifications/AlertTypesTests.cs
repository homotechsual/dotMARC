// test/DotMarc.Tests/Notifications/AlertTypesTests.cs
using System.Reflection;
using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class AlertTypesTests
{
    [Fact]
    public void All_ListsTheFourAlertTypesDotMarcRaises()
    {
        Assert.Equal(
            ["MissedReport", "SuspiciousRejectActivity", "TlsrptFailure", "UnexpectedActivityOnNullRoutedDomain"],
            AlertTypes.All.Select(alertType => alertType.Key));
    }

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
    public void EveryAlertType_HasANameADescriptionAndCreatesTicketsByDefault()
    {
        foreach (var alertType in AlertTypes.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(alertType.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(alertType.Description));
            Assert.True(alertType.CreatesTicketByDefault);
        }
    }

    [Fact]
    public void Find_ReturnsTheAlertType_OrNullForAnUnknownKey()
    {
        Assert.Equal("TlsrptFailure", AlertTypes.Find("TlsrptFailure")!.Key);
        Assert.Null(AlertTypes.Find("NotARealAlertType"));
    }
}
