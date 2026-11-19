using DotMarc.DevBuild;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DotMarc.Tests.DevBuild;

public sealed class DevBuildOptionsTests
{
    [Fact]
    public void IsOffByDefault()
    {
        Assert.False(new DevBuildOptions().Enabled);
    }

    [Fact]
    public void BindsFromTheDevBuildSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DevBuild:Enabled"] = "true" })
            .Build();
        var options = new DevBuildOptions();

        configuration.GetSection(DevBuildOptions.SectionName).Bind(options);

        Assert.True(options.Enabled);
    }

    [Fact]
    public void ReleasedBuild_LinksVersionToItsReleaseNotes()
    {
        var label = DevBuildOptions.VersionLabel("0.12.1", isDevBuild: false);

        Assert.Equal("v0.12.1", label.Text);
        Assert.Equal("https://dotmarc.app/releases-updates/v0-12-1", label.ReleaseNotesUrl);
    }

    [Fact]
    public void DevBuild_ShowsTheTargetVersionAsDev_WithNoReleaseNotesLink()
    {
        var label = DevBuildOptions.VersionLabel("0.13.0", isDevBuild: true);

        Assert.Equal("v0.13.0 (dev)", label.Text);
        Assert.Null(label.ReleaseNotesUrl);
    }
}
