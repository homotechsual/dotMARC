using DotMarc.Security;
using Xunit;

namespace DotMarc.Tests.Security;

public sealed class SignInFailureTests
{
    [Theory]
    [InlineData("Message contains error: 'consent_required', error_description: 'AADSTS65001: The user or administrator has not consented to use the application.', error_uri: 'error_uri is null'.", "AADSTS65001")]
    [InlineData("Message contains error: 'access_denied', error_description: 'AADSTS90094: The grant requires admin permission.', error_uri: 'error_uri is null'.", "AADSTS90094")]
    [InlineData("Message contains error: 'consent_required', error_description: 'error_description is null', error_uri: 'error_uri is null'.", "consent_required")]
    public void MissingAdminConsent_IsRecognised(string message, string code)
    {
        var failure = SignInFailure.Classify(message);

        Assert.Equal((SignInFailureKind.AdminConsentRequired, code), (failure.Kind, failure.Code));
    }

    [Theory]
    [InlineData("Message contains error: 'access_denied', error_description: 'AADSTS65004: User declined to consent to access the app.', error_uri: 'error_uri is null'.", "AADSTS65004")]
    [InlineData("Message contains error: 'access_denied', error_description: 'error_description is null', error_uri: 'error_uri is null'.", "access_denied")]
    public void ADeclinedOrCancelledSignIn_IsRecognised(string message, string code)
    {
        var failure = SignInFailure.Classify(message);

        Assert.Equal((SignInFailureKind.Cancelled, code), (failure.Kind, failure.Code));
    }

    [Fact]
    public void AnyOtherEntraError_KeepsItsCode()
    {
        var failure = SignInFailure.Classify("Message contains error: 'invalid_client', error_description: 'AADSTS7000215: Invalid client secret provided.', error_uri: 'error_uri is null'.");

        Assert.Equal((SignInFailureKind.Other, "AADSTS7000215"), (failure.Kind, failure.Code));
    }

    [Theory]
    [InlineData("Correlation failed.")]
    [InlineData(null)]
    public void AFailureWithNoEntraCode_HasNoCode(string? message)
    {
        var failure = SignInFailure.Classify(message);

        Assert.Equal((SignInFailureKind.Other, null), (failure.Kind, failure.Code));
    }

    [Fact]
    public void TheProblemPageLink_CarriesTheReasonAndCode()
    {
        Assert.Equal("/SignInProblem?reason=admin-consent&code=AADSTS65001",
            new SignInFailure(SignInFailureKind.AdminConsentRequired, "AADSTS65001").ProblemPagePath);
        Assert.Equal("/SignInProblem?reason=other", new SignInFailure(SignInFailureKind.Other, null).ProblemPagePath);
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/", "contoso-tenant", "client-1", "https://login.microsoftonline.com/contoso-tenant/adminconsent?client_id=client-1")]
    [InlineData("https://login.microsoftonline.com", "contoso-tenant", "client-1", "https://login.microsoftonline.com/contoso-tenant/adminconsent?client_id=client-1")]
    [InlineData(null, "contoso-tenant", "client-1", "https://login.microsoftonline.com/contoso-tenant/adminconsent?client_id=client-1")]
    public void TheAdminConsentLink_UsesTheConfiguredTenantAndApp(string? instance, string tenantId, string clientId, string expected)
    {
        Assert.Equal(expected, SignInFailure.AdminConsentUrl(instance, tenantId, clientId));
    }

    [Fact]
    public void WithoutATenantOrApp_ThereIsNoAdminConsentLink()
    {
        Assert.Null(SignInFailure.AdminConsentUrl(null, "", "client-1"));
        Assert.Null(SignInFailure.AdminConsentUrl(null, "contoso-tenant", null));
    }
}
