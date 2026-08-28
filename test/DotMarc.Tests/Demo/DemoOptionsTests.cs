using System.ComponentModel.DataAnnotations;
using DotMarc.Demo;
using Xunit;

namespace DotMarc.Tests.Demo;

public sealed class DemoOptionsTests
{
    [Fact]
    public void DefaultsToDisabled_WithA4AmUtcResetHour()
    {
        var options = new DemoOptions();

        Assert.False(options.Enabled);
        Assert.Equal(4, options.ResetHourUtc);
    }

    /// <summary>DemoDataResetService.GetDelayUntilNextReset builds a DateTimeOffset straight from
    /// ResetHourUtc, outside any try/catch in BackgroundService.ExecuteAsync's loop — an
    /// out-of-range value throws ArgumentOutOfRangeException there and crashes the whole host
    /// under the default BackgroundServiceExceptionBehavior.StopHost. Program.cs wires
    /// ValidateDataAnnotations().ValidateOnStart() for DemoOptions so a misconfigured value fails
    /// fast at startup instead; this test checks the [Range] attribute that makes that possible,
    /// without needing to spin up a full host.</summary>
    [Theory]
    [InlineData(24)]
    [InlineData(-1)]
    [InlineData(100)]
    public void ResetHourUtc_OutOfRange_FailsValidation(int resetHourUtc)
    {
        var options = new DemoOptions { ResetHourUtc = resetHourUtc };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(DemoOptions.ResetHourUtc)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(23)]
    public void ResetHourUtc_InRange_PassesValidation(int resetHourUtc)
    {
        var options = new DemoOptions { ResetHourUtc = resetHourUtc };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.True(isValid);
        Assert.Empty(results);
    }
}
