namespace DotMarc.Data;

/// <summary>Binds the InitialAdmins:Emails configuration section — a comma-separated list of
/// emails granted the Admin role the very first time the app starts with an empty UserAccess
/// table (see AccessBootstrapper). Deliberately not validated/required like GraphOptions: an
/// empty or absent value is a completely valid state on every startup after the first one.</summary>
public sealed class InitialAdminsOptions
{
    public const string SectionName = "InitialAdmins";
    public string Emails { get; set; } = "";
}
