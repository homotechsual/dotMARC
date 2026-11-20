using DotMarc.Components.Shared;
using DotMarc.Demo;
using DotMarc.DevBuild;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotMarc.Tests.Components;

public sealed class AppTitleTests
{
    [Theory]
    [InlineData(false, false, "dotMARC - Dashboard")]
    [InlineData(true, false, "[Demo] dotMARC - Dashboard")]
    [InlineData(false, true, "[Next] dotMARC - Dashboard")]
    [InlineData(true, true, "[Next] dotMARC - Dashboard")]
    public void BuildFlags_PrefixTheExistingTitle_WithNextTakingPrecedence(bool demo, bool next, string expected)
    {
        var title = new AppTitle(Options.Create(new DemoOptions { Enabled = demo }),
            Options.Create(new DevBuildOptions { Enabled = next }));

        Assert.Equal(expected, title.With("dotMARC - Dashboard"));
    }
}
