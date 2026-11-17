using Azure;
using DotMarc.MtaSts;
using Xunit;

namespace DotMarc.Tests.MtaSts;

public sealed class MtaStsProvisioningErrorTests
{
    private const string Hostname = "mta-sts.contoso.io";

    private static RequestFailedException AzureFailure(int status, string? code, string message = "Something happened. Status: 0 Headers: Pragma: no-cache") =>
        new(status, message, code, null);

    [Theory]
    [InlineData(403, "AuthorizationFailed", "managed identity isn't allowed")]
    [InlineData(401, null, "managed identity isn't allowed")]
    [InlineData(404, "ResourceNotFound", "couldn't find the Container App")]
    [InlineData(429, "TooManyRequests", "rate-limiting")]
    [InlineData(503, "ServiceUnavailable", "temporary problem")]
    [InlineData(409, "ExistingManagedCertificateOperationInProgress", "already issuing a certificate for mta-sts.contoso.io")]
    public void KnownAzureFailures_SayWhatToDo(int status, string? code, string expected)
    {
        var text = MtaStsProvisioningError.Describe(AzureFailure(status, code), Hostname);

        Assert.Contains(expected, text);
        Assert.DoesNotContain("Pragma", text);
    }

    [Fact]
    public void AnUnknownAzureFailure_KeepsTheReason_AndTheErrorCode_ButDropsHeaders()
    {
        var text = MtaStsProvisioningError.Describe(AzureFailure(400, "SomethingNew", "The thing was wrong. Status: 400 (Bad Request) ErrorCode: SomethingNew Headers: X: y"), Hostname);

        Assert.Contains("The thing was wrong.", text);
        Assert.Contains("(Azure error SomethingNew)", text);
        Assert.DoesNotContain("Headers", text);
    }

    [Fact]
    public void AMissingErrorCode_FallsBackToTheStatus() =>
        Assert.Contains("(Azure status 418)", MtaStsProvisioningError.Describe(AzureFailure(418, null), Hostname));

    [Fact]
    public void ANetworkFailure_SaysItIsUsuallyTemporary() =>
        Assert.Contains("couldn't reach Azure", MtaStsProvisioningError.Describe(new HttpRequestException("boom"), Hostname));

    [Fact]
    public void OtherExceptions_KeepTheirOwnMessage_WhichIsAlreadyWrittenForPeople() =>
        Assert.Equal("Missing ownership verification record - add it.", MtaStsProvisioningError.Describe(new InvalidOperationException("Missing ownership verification record - add it."), Hostname));
}
