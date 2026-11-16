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
    [InlineData(DmarcCheckStatus.MissingAuthorizationRecord, Color.Success, "OK")]
    [InlineData(DmarcCheckStatus.OtherReportAddresses, Color.Info, "OK, other rua")]
    [InlineData(DmarcCheckStatus.TooManyReportAddresses, Color.Warning, "Too many rua addresses")]
    [InlineData(DmarcCheckStatus.NotChecked, Color.Default, "Not checked yet")]
    public void GetColorAndGetLabel_MapEveryStatusToItsExpectedPresentation(DmarcCheckStatus status, Color expectedColor, string expectedLabel)
    {
        Assert.Equal(expectedColor, DmarcStatusPresentation.GetColor(status));
        Assert.Equal(expectedLabel, DmarcStatusPresentation.GetLabel(status));
    }

    [Theory]
    [InlineData(DmarcCheckStatus.Ok, DmarcAuthorizationCheckStatus.Ok, Color.Success, "OK")]
    [InlineData(DmarcCheckStatus.Ok, DmarcAuthorizationCheckStatus.NotApplicable, Color.Success, "OK")]
    [InlineData(DmarcCheckStatus.Ok, DmarcAuthorizationCheckStatus.Missing, Color.Warning, "Missing authorization")]
    [InlineData(DmarcCheckStatus.MissingOwnRecord, DmarcAuthorizationCheckStatus.Missing, Color.Error, "No DMARC record")]
    [InlineData(DmarcCheckStatus.OtherReportAddresses, DmarcAuthorizationCheckStatus.Ok, Color.Info, "OK, other rua")]
    [InlineData(DmarcCheckStatus.OtherReportAddresses, DmarcAuthorizationCheckStatus.Missing, Color.Warning, "Missing authorization")]
    [InlineData(DmarcCheckStatus.TooManyReportAddresses, DmarcAuthorizationCheckStatus.Ok, Color.Warning, "Too many rua addresses")]
    public void GetColorAndGetLabel_CombinedOverload_PrioritizesOwnRecordOverAuthorization(DmarcCheckStatus status, DmarcAuthorizationCheckStatus authorizationStatus, Color expectedColor, string expectedLabel)
    {
        Assert.Equal(expectedColor, DmarcStatusPresentation.GetColor(status, authorizationStatus));
        Assert.Equal(expectedLabel, DmarcStatusPresentation.GetLabel(status, authorizationStatus));
    }
}
