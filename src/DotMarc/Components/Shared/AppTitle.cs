using DotMarc.Demo;
using DotMarc.DevBuild;
using Microsoft.Extensions.Options;

namespace DotMarc.Components.Shared;

public sealed class AppTitle(IOptions<DemoOptions> demo, IOptions<DevBuildOptions> devBuild)
{
    public string Prefix => devBuild.Value.Enabled ? "[Next] " : demo.Value.Enabled ? "[Demo] " : "";

    public string With(string title) => $"{Prefix}{title}";
}
