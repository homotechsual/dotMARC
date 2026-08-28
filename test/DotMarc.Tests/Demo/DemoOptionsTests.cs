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
}
