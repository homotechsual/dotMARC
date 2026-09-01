using DotMarc.DnsPush;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DnsPushStateProtectorTests
{
    private static DnsPushStateProtector CreateProtector() =>
        new(DataProtectionProvider.Create("DotMarc.Tests"));

    [Fact]
    public void Protect_ThenUnprotect_RoundTripsTheOriginalValues()
    {
        var protector = CreateProtector();
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        var protectedState = protector.Protect(42, "mta-sts", "test-verifier", now);
        var result = protector.Unprotect(protectedState, now);

        Assert.NotNull(result);
        Assert.Equal(42, result!.DomainId);
        Assert.Equal("mta-sts", result.PushTarget);
        Assert.Equal("test-verifier", result.CodeVerifier);
    }

    [Fact]
    public void Unprotect_ReturnsNull_ForATamperedValue()
    {
        var protector = CreateProtector();
        var now = DateTimeOffset.UtcNow;
        var protectedState = protector.Protect(42, "mta-sts", "test-verifier", now);

        var result = protector.Unprotect(protectedState + "tampered", now);

        Assert.Null(result);
    }

    [Fact]
    public void Unprotect_ReturnsNull_OnceExpired()
    {
        var protector = CreateProtector();
        var issuedAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var protectedState = protector.Protect(42, "mta-sts", "test-verifier", issuedAt);

        var result = protector.Unprotect(protectedState, issuedAt.AddMinutes(10));

        Assert.Null(result);
    }
}
