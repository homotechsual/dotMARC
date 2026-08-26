using DotMarc.Data;
using DotMarc.Reporting;
using MudBlazor;
using Xunit;

namespace DotMarc.Tests.Reporting;

public sealed class DmarcStatusPresentationTests
{
    [Theory]
    [InlineData(DmarcCheckStatus.Ok, Color.Success, "OK")]
    [InlineData(DmarcCheckStatus.MissingOwnRecord, Color.Error, "No DMARC record")]
    [InlineData(DmarcCheckStatus.Misconfigured, Color.Error, "Misconfigured")]
    [InlineData(DmarcCheckStatus.MissingAuthorizationRecord, Color.Warning, "Missing authorization")]
    [InlineData(DmarcCheckStatus.NotChecked, Color.Default, "Not checked yet")]
    public void GetColorAndGetLabel_MapEveryStatusToItsExpectedPresentation(DmarcCheckStatus status, Color expectedColor, string expectedLabel)
    {
        Assert.Equal(expectedColor, DmarcStatusPresentation.GetColor(status));
        Assert.Equal(expectedLabel, DmarcStatusPresentation.GetLabel(status));
    }
}
