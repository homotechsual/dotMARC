namespace DotMarc.DevBuild;

/// <summary>Marks an instance as running unreleased code (the hosted "next" build). When Enabled, every layout shows a banner
/// saying so and the footer labels the version as a dev build. Off by default, so a normal install is unchanged.</summary>
public sealed class DevBuildOptions
{
    public const string SectionName = "DevBuild";

    public bool Enabled { get; set; }

    /// <summary>The footer's version text, and where it links. An unreleased version has no release notes yet, so a dev build
    /// gets no link rather than one that goes nowhere.</summary>
    public static VersionLabel VersionLabel(string version, bool isDevBuild) =>
        isDevBuild
            ? new VersionLabel($"v{version} (dev)", null)
            : new VersionLabel($"v{version}", $"https://dotmarc.app/releases-updates/v{version.Replace('.', '-')}");
}

public sealed record VersionLabel(string Text, string? ReleaseNotesUrl);
