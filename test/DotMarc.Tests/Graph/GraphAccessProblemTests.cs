using System.Net;
using System.Text;
using DotMarc.Graph;
using Xunit;

namespace DotMarc.Tests.Graph;

public sealed class GraphAccessProblemTests
{
    private const string Mailbox = "dmarc-reports@example.com";
    private const string ConsentUrl = "https://login.microsoftonline.com/tenant-id/adminconsent?client_id=client-id";

    /// <summary>An unsigned token carrying just the roles claim, which is all GraphAccessProblem reads.</summary>
    public static string TokenWithRoles(params string[] roles)
    {
        static string Encode(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = roles.Length == 0 ? """{"aud":"https://graph.microsoft.com"}""" : $$"""{"roles":[{{string.Join(",", roles.Select(role => $"\"{role}\""))}}]}""";
        return $"{Encode("""{"alg":"none"}""")}.{Encode(payload)}.signature";
    }

    private static string? Describe(HttpStatusCode status, string token, string? graphErrorCode, bool isWrite = false) =>
        GraphAccessProblem.Describe(status, token, graphErrorCode, Mailbox, "tenant-id", "client-id", isWrite);

    [Fact]
    public void ATokenWithNoMailPermission_MeansAdminConsentOrThePermissionIsMissing()
    {
        var message = Describe(HttpStatusCode.Forbidden, TokenWithRoles(), "ErrorAccessDenied");

        Assert.NotNull(message);
        Assert.Contains("admin consent", message);
        Assert.Contains("Mail.ReadWrite", message);
        Assert.Contains(ConsentUrl, message);
    }

    [Fact]
    public void ATokenWithReadOnly_CantMarkMessagesRead()
    {
        var message = Describe(HttpStatusCode.Forbidden, TokenWithRoles("Mail.Read"), "ErrorAccessDenied", isWrite: true);

        Assert.NotNull(message);
        Assert.Contains("Mail.ReadWrite", message);
        Assert.Contains("mark", message);
        Assert.Contains(ConsentUrl, message);
    }

    [Theory]
    [InlineData("Mail.ReadWrite", false)]
    [InlineData("Mail.ReadWrite", true)]
    [InlineData("Mail.Read", false)]
    public void WithTheRightPermission_AnAccessDenial_PointsAtTheMailboxAccessPolicy(string role, bool isWrite)
    {
        var message = Describe(HttpStatusCode.Forbidden, TokenWithRoles(role), "ErrorAccessDenied", isWrite);

        Assert.NotNull(message);
        Assert.Contains("application access policy", message);
        Assert.Contains(Mailbox, message);
        Assert.DoesNotContain("adminconsent", message);
    }

    [Theory]
    [InlineData("ErrorInvalidUser")]
    [InlineData("MailboxNotEnabledForRESTAPI")]
    public void AMissingMailbox_SaysTheAddressIsWrong(string code)
    {
        var message = Describe(HttpStatusCode.NotFound, TokenWithRoles("Mail.ReadWrite"), code);

        Assert.NotNull(message);
        Assert.Contains(Mailbox, message);
        Assert.Contains("mailbox address", message);
    }

    [Fact]
    public void AnotherNotFound_IsLeftAlone()
    {
        Assert.Null(Describe(HttpStatusCode.NotFound, TokenWithRoles("Mail.ReadWrite"), "ErrorItemNotFound"));
    }

    [Fact]
    public void AnUnauthorizedResponse_IsDiagnosedTheSameWay()
    {
        Assert.Contains("admin consent", Describe(HttpStatusCode.Unauthorized, TokenWithRoles(), null));
    }

    [Fact]
    public void ATokenThatCantBeRead_StillGetsAHelpfulMessage()
    {
        var message = Describe(HttpStatusCode.Forbidden, "not-a-jwt", "ErrorAccessDenied");

        Assert.NotNull(message);
        Assert.Contains("admin consent", message);
        Assert.Contains("application access policy", message);
    }

    [Fact]
    public void TheTokenNeverAppearsInTheMessage()
    {
        var token = TokenWithRoles();

        Assert.DoesNotContain(token.Split('.')[1], Describe(HttpStatusCode.Forbidden, token, "ErrorAccessDenied"));
    }

    [Fact]
    public void OtherFailures_AreLeftAlone()
    {
        Assert.Null(Describe(HttpStatusCode.InternalServerError, TokenWithRoles(), null));
        Assert.Null(Describe(HttpStatusCode.TooManyRequests, TokenWithRoles(), null));
    }
}
