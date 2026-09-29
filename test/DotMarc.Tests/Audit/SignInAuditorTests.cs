using System.Security.Claims;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Audit;

[Collection("Postgres")]
public sealed class SignInAuditorTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public SignInAuditorTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = new FakeDbContextFactory(_connectionString).CreateDbContext();
        await context.Database.MigrateAsync();
        context.Roles.Add(new Role { Name = "Reader", Permissions = [Permission.DomainsView] });
        await context.SaveChangesAsync();
        var roleId = (await context.Roles.SingleAsync()).Id;
        await UserAccessManagementService.GrantAccessAsync(context, TestActors.Admin, "sam@contoso.com", roleId, []);
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private SignInAuditor CreateAuditor()
    {
        var factory = new FakeDbContextFactory(_connectionString);
        return new SignInAuditor(factory, new AuditRecorder(factory, NullLogger<AuditRecorder>.Instance), NullLogger<SignInAuditor>.Instance);
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Test", nameType: ClaimTypes.Name, roleType: ClaimTypes.Role));

    private async Task<AuditEntry> SingleSignInEntryAsync()
    {
        await using var verify = new FakeDbContextFactory(_connectionString).CreateDbContext();
        return await verify.AuditEntries.SingleAsync(entry => entry.Kind == AuditEntryKind.SignIn);
    }

    [Fact]
    public async Task RecordAsync_RecordsASuccessfulSignIn_ForSomeoneWithAGrant()
    {
        await CreateAuditor().RecordAsync(Principal(new Claim("preferred_username", "sam@contoso.com"), new Claim(ClaimTypes.Name, "Sam Jones")));

        var entry = await SingleSignInEntryAsync();
        Assert.Equal((AuditActions.SignInSucceeded, "Sam Jones", "sam@contoso.com", "Sam Jones signed in"), (entry.Action, entry.ActorName, entry.ActorEmail, entry.Summary));
    }

    [Fact]
    public async Task RecordAsync_RecordsARefusal_WithTheEmailTheyTried()
    {
        await CreateAuditor().RecordAsync(Principal(new Claim("preferred_username", "stranger@fabrikam.com")));

        var entry = await SingleSignInEntryAsync();
        Assert.Equal((AuditActions.SignInRefused, "stranger@fabrikam.com"), (entry.Action, entry.ActorEmail));
    }

    [Fact]
    public async Task RecordAsync_RecordsAnUnknownUserAsRefused_WhenTheTokenHasNoIdentityClaims()
    {
        await CreateAuditor().RecordAsync(new ClaimsPrincipal(new ClaimsIdentity()));

        var entry = await SingleSignInEntryAsync();
        Assert.Equal((AuditActions.SignInRefused, "Unknown user"), (entry.Action, entry.ActorName));
    }
}
